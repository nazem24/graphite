using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Graphite.Core.Canvas;

/// <summary>
/// Turns Canvas API answers (JSON) into Graphite's own models. Pure: no network, so it is unit-tested
/// with canned answers.
/// </summary>
public static class CanvasParser
{
    // ------------------------------------------------------------- JSON helpers

    public static string? Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static long? Long(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return null;
    }

    public static double? Double(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        return null;
    }

    public static bool Bool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static DateTime? Date(JsonElement el, string name)
    {
        string? s = Str(el, name);
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    private static JsonElement Prop(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    // ------------------------------------------------------------- courses

    /// <summary>Courses from <c>/courses?include[]=term</c>. Entries without a name (access
    /// restricted by date) are skipped; the same course listed twice is kept once.</summary>
    public static List<CanvasCourse> ParseCourses(IEnumerable<JsonElement> items)
    {
        var map = new Dictionary<string, CanvasCourse>();
        foreach (var c in items)
        {
            string? id = Str(c, "id");
            string? name = Str(c, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(name)) continue;
            var term = Prop(c, "term");
            map[id] = new CanvasCourse
            {
                Id = id,
                Name = name.Trim(),
                Code = Str(c, "course_code") ?? "",
                TermId = Str(c, "enrollment_term_id") ?? "0",
                TermName = Str(term, "name") ?? "",
                TermStartUtc = Date(term, "start_at"),
                TermEndUtc = Date(term, "end_at"),
            };
        }
        return map.Values.ToList();
    }

    /// <summary>The distinct terms of a course list, newest first.</summary>
    public static List<CanvasTerm> Terms(IEnumerable<CanvasCourse> courses)
    {
        var map = new Dictionary<string, CanvasTerm>();
        foreach (var c in courses)
            if (!map.ContainsKey(c.TermId))
                map[c.TermId] = new CanvasTerm(c.TermId, c.TermName.Length > 0 ? c.TermName : "Term " + c.TermId,
                    c.TermStartUtc, c.TermEndUtc);
        return map.Values
            .OrderByDescending(t => t.StartUtc ?? DateTime.MinValue)
            .ThenByDescending(t => long.TryParse(t.Id, out long n) ? n : 0)
            .ToList();
    }

    /// <summary>Which term to show: the preferred one if it is still listed, else the term running today,
    /// else the newest. Old courses often stay "active" in Canvas, which is why this filters by term
    /// rather than by enrolment state.</summary>
    public static string PickTerm(IReadOnlyCollection<CanvasCourse> courses, DateTime nowUtc, string? preferredId)
    {
        var terms = Terms(courses);
        if (!string.IsNullOrEmpty(preferredId) && terms.Any(t => t.Id == preferredId)) return preferredId;
        var running = terms.FirstOrDefault(t => t.StartUtc is { } s && s <= nowUtc && (t.EndUtc is not { } e || e >= nowUtc));
        if (running != null) return running.Id;
        // A term that has not started yet but is the next one up beats an old one (the weeks before term).
        var next = terms.Where(t => t.StartUtc is { } s && s > nowUtc).OrderBy(t => t.StartUtc).FirstOrDefault();
        if (next != null && (next.StartUtc!.Value - nowUtc).TotalDays < 21) return next.Id;
        // Otherwise the newest term that has a real date; courses without one ("Default term") come last.
        return terms.FirstOrDefault(t => t.StartUtc != null)?.Id ?? terms.FirstOrDefault()?.Id ?? "";
    }

    // ------------------------------------------------------------- files

    /// <summary>A file object (<c>/files/:id</c>) or a module item's <c>content_details</c>.</summary>
    public static CanvasFileInfo? ParseFileInfo(JsonElement el, string fallbackName = "")
    {
        string? url = Str(el, "url");
        if (string.IsNullOrEmpty(url)) return null;
        return new CanvasFileInfo
        {
            Url = url,
            Name = Str(el, "display_name") ?? Str(el, "filename") ?? fallbackName,
            Size = Long(el, "size"),
            Mime = Str(el, "content-type") ?? "",
            UpdatedUtc = Date(el, "updated_at") ?? Date(el, "modified_at"),
        };
    }

    // ------------------------------------------------------------- modules

    /// <summary>One module item (<c>/modules?include[]=items&amp;include[]=content_details</c>).
    /// <paramref name="pageBody"/> gives the HTML of a wiki page by its url slug, so files linked
    /// inside pages show up under the page.</summary>
    public static CanvasItem ParseModuleItem(JsonElement i, Func<string, string?> pageBody, string baseUrl)
    {
        var cd = Prop(i, "content_details");
        string id = Str(i, "id") ?? Guid.NewGuid().ToString("N");
        var e = new CanvasItem
        {
            Key = "item:" + id,
            Title = (Str(i, "title") ?? "").Trim() is { Length: > 0 } t ? t : "(untitled)",
            Indent = (int)(Long(i, "indent") ?? 0),
            Url = Str(i, "html_url") ?? "",
            Locked = Bool(cd, "locked_for_user"),
            DueUtc = Date(cd, "due_at"),
            Points = Double(cd, "points_possible"),
        };
        switch (Str(i, "type"))
        {
            case "SubHeader":
                e.Kind = CanvasItemKind.Header;
                break;
            case "File":
                e.Kind = CanvasItemKind.File;
                string? fileId = Str(i, "content_id");
                if (fileId != null) e.Key = "file:" + fileId;
                e.File = new CanvasFileRef
                {
                    Id = fileId ?? "",
                    Href = e.Url,
                    Direct = ParseFileInfo(cd, e.Title),
                };
                break;
            case "Page":
                e.Kind = CanvasItemKind.Page;
                string? slug = Str(i, "page_url");
                e.Links = CanvasHtml.ExtractLinks(slug == null ? null : pageBody(slug), baseUrl);
                break;
            case "Assignment": e.Kind = CanvasItemKind.Assignment; break;
            case "Quiz": e.Kind = CanvasItemKind.Quiz; break;
            case "Discussion": e.Kind = CanvasItemKind.Discussion; break;
            case "ExternalUrl":
                e.Kind = CanvasItemKind.Link;
                string? ext = Str(i, "external_url");
                if (!string.IsNullOrEmpty(ext)) e.Url = ext;
                e.Key = "link:" + (ext ?? id);
                break;
            case "ExternalTool":
                e.Kind = CanvasItemKind.Tool;
                e.Url = Str(i, "external_url") is { Length: > 0 } tool ? tool : e.Url;
                break;
        }
        return e;
    }

    public static CanvasModule ParseModule(JsonElement m, IEnumerable<JsonElement> items, Func<string, string?> pageBody, string baseUrl) => new()
    {
        Id = Str(m, "id") ?? "",
        Name = (Str(m, "name") ?? "").Trim(),
        UnlockUtc = Date(m, "unlock_at"),
        Items = items.Select(i => ParseModuleItem(i, pageBody, baseUrl)).ToList(),
    };

    // ------------------------------------------------------------- errors

    /// <summary>The message in a Canvas error answer ({"errors":[{"message":…}]} or {"message":…}).</summary>
    public static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("errors", out var errs) &&
                errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0 && Str(errs[0], "message") is { } m)
                return m;
            return Str(root, "message");
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Finds files and links inside the HTML of a Canvas wiki page. Canvas marks files it hosts
/// with <c>data-api-returntype="File"</c> and a <c>data-api-endpoint</c>; everything else that points to
/// a web page becomes a link.</summary>
public static class CanvasHtml
{
    private static readonly Regex Anchor = new(@"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"(?<name>[\w:-]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
        RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex FileId = new(@"/files/(\d+)", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static List<CanvasItem> ExtractLinks(string? html, string baseUrl)
    {
        var output = new List<CanvasItem>();
        if (string.IsNullOrEmpty(html)) return output;
        Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri);
        var seen = new HashSet<string>();

        foreach (Match a in Anchor.Matches(html))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Attr.Matches(a.Groups["attrs"].Value))
                attrs[m.Groups["name"].Value] = WebUtility.HtmlDecode(m.Groups["v"].Value);
            if (!attrs.TryGetValue("href", out string? href) || string.IsNullOrWhiteSpace(href)) continue;

            string text = Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(a.Groups["text"].Value, " ")), " ").Trim();
            attrs.TryGetValue("data-api-endpoint", out string? endpoint);
            attrs.TryGetValue("data-api-returntype", out string? returnType);

            Uri? abs;
            if (baseUri != null) Uri.TryCreate(baseUri, href.Trim(), out abs);
            else Uri.TryCreate(href.Trim(), UriKind.Absolute, out abs);
            if (abs == null || (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps)) continue;
            string absText = abs.AbsoluteUri;

            Match fm = FileId.Match(endpoint ?? "");
            bool sameHost = baseUri != null && string.Equals(abs.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);
            if (!fm.Success && (string.Equals(returnType, "File", StringComparison.OrdinalIgnoreCase) || sameHost))
                fm = FileId.Match(abs.AbsolutePath);

            if (fm.Success)
            {
                string key = "file:" + fm.Groups[1].Value;
                if (!seen.Add(key)) continue;
                output.Add(new CanvasItem
                {
                    Key = key,
                    Kind = CanvasItemKind.File,
                    Title = text.Length > 0 ? text : "File " + fm.Groups[1].Value,
                    Url = absText,
                    File = new CanvasFileRef { Id = fm.Groups[1].Value, Endpoint = endpoint, Href = absText },
                });
            }
            else
            {
                // a link back to the Canvas home page is navigation chrome, not content
                if (baseUri != null && absText.Split('#')[0] == baseUri.AbsoluteUri) continue;
                string key = "link:" + absText;
                if (!seen.Add(key)) continue;
                output.Add(new CanvasItem { Key = key, Kind = CanvasItemKind.Link, Title = text.Length > 0 ? text : absText, Url = absText });
            }
        }
        return output;
    }
}

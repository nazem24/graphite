using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Graphite.Core.Canvas;

/// <summary>What a calendar entry is: a lecture (or any calendar event), a Canvas deadline, or an exam.</summary>
public enum CalendarKind { Lecture, Deadline, Exam, Notice }

/// <summary>One thing on the calendar. Times are local time on this PC.</summary>
public sealed class CalendarEntry
{
    public string Id { get; set; } = "";
    public CalendarKind Kind { get; set; }
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AllDay { get; set; }
    public string Location { get; set; } = "";
    /// <summary>The Canvas course it belongs to, when known (always for deadlines, worked out for lectures).</summary>
    public string? CourseId { get; set; }
    public string CourseName { get; set; } = "";
    /// <summary>Where to open it (deadlines: the assignment in Canvas).</summary>
    public string Url { get; set; } = "";
}

/// <summary>Reads Canvas' calendar events (assignments with a due date) into <see cref="CalendarEntry"/> items.</summary>
public static class CanvasCalendarParser
{
    /// <summary>Entries of <c>/calendar_events?type=assignment</c>. <paramref name="courses"/> maps "course_123" to the course.
    /// Events without a date are left out.</summary>
    public static List<CalendarEntry> ParseDeadlines(IEnumerable<JsonElement> items, IReadOnlyDictionary<string, CanvasCourse> courses)
    {
        var result = new List<CalendarEntry>();
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            JsonElement assignment = e.TryGetProperty("assignment", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            DateTime? due = CanvasParser.Date(assignment, "due_at") ?? CanvasParser.Date(e, "start_at") ?? CanvasParser.Date(e, "end_at");
            if (due == null) continue;

            string title = CanvasParser.Str(e, "title") is { Length: > 0 } t ? t
                : CanvasParser.Str(assignment, "name") is { Length: > 0 } n ? n : "Assignment";
            string code = CanvasParser.Str(e, "context_code") ?? "";
            courses.TryGetValue(code, out var course);
            string courseId = course?.Id ?? (code.StartsWith("course_", StringComparison.Ordinal) ? code["course_".Length..] : "");
            DateTime local = due.Value.ToLocalTime();
            result.Add(new CalendarEntry
            {
                Id = "due:" + (CanvasParser.Str(e, "id") ?? title + "|" + due.Value.Ticks.ToString(CultureInfo.InvariantCulture)),
                Kind = CalendarKind.Deadline,
                Title = title,
                Start = local,
                End = local,
                CourseId = courseId.Length > 0 ? courseId : null,
                CourseName = course?.Name ?? CanvasParser.Str(e, "context_name") ?? "",
                Url = CanvasParser.Str(e, "html_url") ?? CanvasParser.Str(assignment, "html_url") ?? "",
            });
        }
        return result;
    }
}

/// <summary>Works out which course a lecture belongs to from its title, so entries can be coloured by course.</summary>
public static class CalendarCourseMatcher
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "and", "the", "for", "with", "der", "die", "das", "und", "von", "vom", "zur", "zum", "des", "den", "dem",
        "intro", "introduction", "lecture", "vorlesung", "uebung", "exercise", "tutorial", "seminar", "course",
    };

    private static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static HashSet<string> Words(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4 && !Stop.Contains(w)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The best matching course, or null when nothing in the title points at one.</summary>
    public static CanvasCourse? Match(string? title, IEnumerable<CanvasCourse> courses)
    {
        string nt = Norm(title);
        if (nt.Length < 3) return null;
        var titleWords = Words(nt);
        CanvasCourse? best = null;
        double bestScore = 0;
        foreach (var c in courses)
        {
            string nn = Norm(c.Name), code = Norm(c.Code);
            double score = 0;
            if (nn.Length >= 4 && (nt.Contains(nn, StringComparison.Ordinal) || (nt.Length >= 4 && nn.Contains(nt, StringComparison.Ordinal))))
                score = 1000 + nn.Length;
            else if (code.Length >= 3 && nt.Contains(code, StringComparison.Ordinal))
                score = 900;
            else
            {
                var nameWords = Words(nn);
                int shared = titleWords.Count(w => nameWords.Contains(w));
                if (shared > 0) score = shared * 10 + 5.0 * shared / Math.Max(1, nameWords.Count);
            }
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }
}

/// <summary>A calendar read from an .ics file: its events, ready to be laid out on any range of days.</summary>
public sealed class IcsCalendar
{
    public string Name { get; internal set; } = "";
    internal List<IcsEvent> Events { get; } = new();
    public int EventCount => Events.Count;

    private static readonly Regex ExamWords = new(@"\b(exams?|examination|examen|klausur\w*|prüfung\w*|pruefung\w*|midterm\w*|assessment)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Registration and similar dates: things to do by a day, not an exam you sit.
    private static readonly Regex AdminWords = new(@"anmeld|abmeld|einschreib|rückmeld|rueckmeld|registration|enrol|deadline|\bfrist",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Exam when the title says so (and is not about signing up for one), a notice (something on the calendar that is
    /// neither a lecture nor an exam) for all-day entries and registration dates, a lecture otherwise.</summary>
    internal static CalendarKind Classify(string title, bool allDay)
    {
        bool admin = AdminWords.IsMatch(title);
        if (!admin && ExamWords.IsMatch(title)) return CalendarKind.Exam;
        return admin || allDay ? CalendarKind.Notice : CalendarKind.Lecture;
    }

    /// <summary>The occurrences that touch [from, to), repeating events unfolded. Local times.</summary>
    public List<CalendarEntry> Expand(DateTime fromLocal, DateTime toLocal)
    {
        var result = new List<CalendarEntry>();
        var replaced = new Dictionary<string, HashSet<DateTime>>(StringComparer.Ordinal);
        foreach (var e in Events)
        {
            if (e.RecurrenceIdWall is not { } rid) continue;
            if (!replaced.TryGetValue(e.Uid, out var set)) replaced[e.Uid] = set = new HashSet<DateTime>();
            set.Add(rid);
        }

        void Add(IcsEvent e, DateTime wall)
        {
            DateTime start = e.AllDay ? wall : IcsParser.ToLocal(wall, e.Zone);
            DateTime end = start + e.Duration;
            if (start >= toLocal || (end <= fromLocal && start < fromLocal)) return;
            result.Add(new CalendarEntry
            {
                Id = "ics:" + e.Uid + "|" + start.Ticks.ToString(CultureInfo.InvariantCulture),
                Kind = Classify(e.Summary, e.AllDay),
                Title = e.Summary.Length > 0 ? e.Summary : "(no title)",
                Start = start,
                End = end,
                AllDay = e.AllDay,
                Location = e.Location,
            });
        }

        foreach (var e in Events)
        {
            if (e.Rule == null || e.RecurrenceIdWall != null)
            {
                Add(e, e.StartWall);
                continue;
            }
            replaced.TryGetValue(e.Uid, out var skip);
            foreach (var wall in IcsParser.Occurrences(e, fromLocal, toLocal))
            {
                if (e.ExDates.Contains(wall)) continue;
                if (skip != null && skip.Contains(wall)) continue;
                Add(e, wall);
            }
        }
        return result;
    }
}

internal sealed class IcsEvent
{
    public string Uid = "";
    public string Summary = "";
    public string Location = "";
    /// <summary>Start as written in the file (a wall clock time in <see cref="Zone"/>).</summary>
    public DateTime StartWall;
    /// <summary>Null: floating time (taken as this PC's local time).</summary>
    public TimeZoneInfo? Zone;
    public TimeSpan Duration;
    public bool AllDay;
    public Dictionary<string, string>? Rule;
    public HashSet<DateTime> ExDates = new();
    public DateTime? RecurrenceIdWall;
}

/// <summary>A small .ics reader: events with their time zones, repeats (daily, weekly with weekdays, monthly, yearly,
/// with interval, count and until), excluded dates and changed single occurrences. Cancelled events are skipped.
/// Not supported: monthly rules like "second Tuesday" (they repeat on the same day of the month instead).</summary>
public static class IcsParser
{
    private readonly record struct IcsTime(DateTime Wall, TimeZoneInfo? Zone, bool DateOnly);

    /// <summary>"webcal://…" becomes "https://…"; anything that is not a web address throws.</summary>
    public static string NormalizeUrl(string url)
    {
        string u = (url ?? "").Trim();
        if (u.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u["webcals://".Length..];
        else if (u.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u["webcal://".Length..];
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("That is not a web address.", nameof(url));
        return uri.AbsoluteUri;
    }

    public static IcsCalendar Parse(string text)
    {
        var cal = new IcsCalendar();
        var lines = new List<string>();
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && lines.Count > 0) lines[^1] += line[1..];   // folded line
            else lines.Add(line);
        }

        IcsEvent? cur = null;
        bool inAlarm = false;
        bool cancelled = false;
        IcsTime? start = null, end = null, recId = null;
        TimeSpan? duration = null;
        var exRaw = new List<IcsTime>();

        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                cur = new IcsEvent();
                inAlarm = cancelled = false;
                start = end = recId = null;
                duration = null;
                exRaw.Clear();
                continue;
            }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null && !cancelled && start is { } s) Finish(cal, cur, s, end, duration, recId, exRaw);
                cur = null;
                continue;
            }
            if (line.Equals("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase)) { inAlarm = true; continue; }
            if (line.Equals("END:VALARM", StringComparison.OrdinalIgnoreCase)) { inAlarm = false; continue; }
            if (inAlarm) continue;
            if (SplitLine(line) is not { } p) continue;

            if (cur == null)
            {
                if (p.Name == "X-WR-CALNAME" && cal.Name.Length == 0) cal.Name = Unescape(p.Value);
                continue;
            }
            switch (p.Name)
            {
                case "UID": cur.Uid = p.Value.Trim(); break;
                case "SUMMARY": cur.Summary = Unescape(p.Value).Trim(); break;
                case "LOCATION": cur.Location = Unescape(p.Value).Trim(); break;
                case "STATUS": cancelled = p.Value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "DTSTART": start = ParseTime(p.Value, p.Params); break;
                case "DTEND": end = ParseTime(p.Value, p.Params); break;
                case "DURATION": duration = ParseDuration(p.Value); break;
                case "RECURRENCE-ID": recId = ParseTime(p.Value, p.Params); break;
                case "RRULE": cur.Rule = ParseRule(p.Value); break;
                case "EXDATE":
                    foreach (string part in p.Value.Split(','))
                        if (ParseTime(part, p.Params) is { } ex) exRaw.Add(ex);
                    break;
            }
        }
        return cal;
    }

    private static void Finish(IcsCalendar cal, IcsEvent e, IcsTime start, IcsTime? end, TimeSpan? duration, IcsTime? recId, List<IcsTime> exRaw)
    {
        e.AllDay = start.DateOnly;
        e.StartWall = start.Wall;
        e.Zone = start.DateOnly ? null : start.Zone;
        if (e.Uid.Length == 0) e.Uid = e.Summary + "|" + start.Wall.Ticks.ToString(CultureInfo.InvariantCulture);

        if (duration is { } d) e.Duration = d;
        else if (end is { } en)
        {
            if (e.AllDay) e.Duration = TimeSpan.FromDays(Math.Max(1, (en.Wall.Date - start.Wall.Date).Days));
            else e.Duration = ToLocal(en.Wall, en.Zone) - ToLocal(start.Wall, start.Zone);
        }
        else e.Duration = e.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
        if (e.Duration < TimeSpan.Zero) e.Duration = TimeSpan.Zero;

        foreach (var ex in exRaw) e.ExDates.Add(e.AllDay ? ex.Wall.Date : WallIn(ex.Wall, ex.Zone, e.Zone));
        if (recId is { } r) e.RecurrenceIdWall = e.AllDay ? r.Wall.Date : WallIn(r.Wall, r.Zone, e.Zone);
        cal.Events.Add(e);
    }

    // ------------------------------------------------------------- lines and values

    private readonly record struct Prop(string Name, Dictionary<string, string> Params, string Value);

    private static Prop? SplitLine(string line)
    {
        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        string[] head = line[..colon].Split(';');
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < head.Length; i++)
        {
            int eq = head[i].IndexOf('=');
            if (eq > 0) ps[head[i][..eq]] = head[i][(eq + 1)..].Trim('"');
        }
        return new Prop(head[0].Trim().ToUpperInvariant(), ps, line[(colon + 1)..]);
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static IcsTime? ParseTime(string value, Dictionary<string, string> ps)
    {
        string v = value.Trim();
        bool dateOnly = (ps.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || v.Length == 8;
        if (dateOnly)
            return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? new IcsTime(day, null, true) : null;

        TimeZoneInfo? zone = null;
        if (v.EndsWith('Z') || v.EndsWith('z'))
        {
            v = v[..^1];
            zone = TimeZoneInfo.Utc;
        }
        else if (ps.TryGetValue("TZID", out var tzid)) zone = FindZone(tzid);

        string[] formats = { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" };
        return DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)
            ? new IcsTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone, false) : null;
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        id = id.Trim();
        if (id.StartsWith('/')) id = string.Join("/", id.Split('/', StringSplitOptions.RemoveEmptyEntries).TakeLast(2));   // /mozilla.org/20050101/Europe/Zurich
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static readonly Regex DurationPattern = new(@"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static TimeSpan? ParseDuration(string value)
    {
        var m = DurationPattern.Match(value.Trim());
        if (!m.Success) return null;
        int Part(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        var span = TimeSpan.FromDays(Part(2) * 7 + Part(3)) + TimeSpan.FromHours(Part(4)) + TimeSpan.FromMinutes(Part(5)) + TimeSpan.FromSeconds(Part(6));
        return m.Groups[1].Value == "-" ? -span : span;
    }

    private static Dictionary<string, string> ParseRule(string value)
    {
        var rule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) rule[part[..eq].Trim().ToUpperInvariant()] = part[(eq + 1)..].Trim();
        }
        return rule;
    }

    // ------------------------------------------------------------- time zones

    /// <summary>A wall clock time in <paramref name="zone"/> as local time on this PC (null zone: already local).</summary>
    internal static DateTime ToLocal(DateTime wall, TimeZoneInfo? zone)
    {
        if (zone == null) return wall;
        try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local); }
        catch (ArgumentException) { return wall; }   // a time that does not exist (the hour skipped by daylight saving)
    }

    /// <summary>A wall clock time of one zone as the wall clock time of another (null: this PC's zone).</summary>
    private static DateTime WallIn(DateTime wall, TimeZoneInfo? from, TimeZoneInfo? to)
    {
        if (from == null || ReferenceEquals(from, to)) return wall;
        try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), from, to ?? TimeZoneInfo.Local); }
        catch (ArgumentException) { return wall; }
    }

    // ------------------------------------------------------------- repeats

    /// <summary>The start times (wall clock, in the event's zone) of a repeating event that can touch [from, to).</summary>
    internal static IEnumerable<DateTime> Occurrences(IcsEvent e, DateTime from, DateTime to)
    {
        var rule = e.Rule!;
        rule.TryGetValue("FREQ", out string? freq);
        int interval = rule.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out int n) && n > 0 ? n : 1;
        int count = rule.TryGetValue("COUNT", out var cv) && int.TryParse(cv, out int c) && c > 0 ? c : int.MaxValue;
        DateTime? until = null;
        if (rule.TryGetValue("UNTIL", out var uv) && ParseTime(uv, new Dictionary<string, string>()) is { } u)
            until = e.AllDay ? u.Wall.Date.AddDays(1).AddTicks(-1) : WallIn(u.Wall, u.Zone, e.Zone);

        DateTime start = e.StartWall;
        DateTime limit = to.AddDays(2);
        DateTime lower = from.AddDays(-2) - e.Duration;
        int produced = 0;

        bool Done(DateTime d) => d > limit || (until != null && d > until);

        switch ((freq ?? "").ToUpperInvariant())
        {
            case "DAILY":
                for (int i = 0; i < 5000; i++)
                {
                    DateTime d = start.AddDays((double)i * interval);
                    if (Done(d) || ++produced > count) yield break;
                    if (d >= lower) yield return d;
                }
                break;

            case "WEEKLY":
            {
                var days = new List<DayOfWeek>();
                if (rule.TryGetValue("BYDAY", out var byDay))
                    foreach (string token in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (WeekDay(token) is { } wd) days.Add(wd);
                if (days.Count == 0) days.Add(start.DayOfWeek);
                days = days.Distinct().OrderBy(x => ((int)x + 6) % 7).ToList();

                DateTime monday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (int w = 0; w < 2000; w++)
                {
                    DateTime week = monday.AddDays(7.0 * w * interval);
                    if (week > limit) yield break;
                    foreach (var day in days)
                    {
                        DateTime d = week.AddDays(((int)day + 6) % 7) + start.TimeOfDay;
                        if (d < start) continue;
                        if (Done(d) || ++produced > count) yield break;
                        if (d >= lower) yield return d;
                    }
                }
                break;
            }

            case "MONTHLY":
                for (int i = 0; i < 1200; i++)
                {
                    DateTime d = start.AddMonths(i * interval);
                    if (Done(d) || ++produced > count) yield break;
                    if (d >= lower) yield return d;
                }
                break;

            case "YEARLY":
                for (int i = 0; i < 200; i++)
                {
                    DateTime d = start.AddYears(i * interval);
                    if (Done(d) || ++produced > count) yield break;
                    if (d >= lower) yield return d;
                }
                break;

            default:
                yield return start;
                break;
        }
    }

    private static DayOfWeek? WeekDay(string token)
    {
        string t = token.Trim().ToUpperInvariant();
        if (t.Length < 2) return null;
        return t[^2..] switch
        {
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            "SU" => DayOfWeek.Sunday,
            _ => null,
        };
    }
}

/// <summary>Downloads an .ics calendar from a web address. No credentials are sent: the address itself is the secret.</summary>
public static class IcsFetcher
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = TimeSpan.FromSeconds(40) };

    private const long MaxBytes = 20_000_000;

    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        string address = IcsParser.NormalizeUrl(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", CanvasClient.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/calendar, text/plain;q=0.9, */*;q=0.5");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The calendar address answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is > MaxBytes)
            throw new FormatException("That file is too large to be a calendar.");
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Length > MaxBytes) throw new FormatException("That file is too large to be a calendar.");
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That address did not give a calendar (.ics) file.");
        return text;
    }
}

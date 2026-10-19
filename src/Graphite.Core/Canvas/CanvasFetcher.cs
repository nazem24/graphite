using System.Collections.Concurrent;
using System.Text.Json;

namespace Graphite.Core.Canvas;

/// <summary>
/// The few things Graphite asks Canvas for. Works around lecturers who hide the Files tab: everything comes
/// from the modules (whose File items carry their download facts), the wiki pages (files linked inside them)
/// and the start page, never from <c>/courses/:id/files</c>, which then claims the files do not exist.
/// </summary>
public static class CanvasFetcher
{
    /// <summary>Who the token belongs to (a cheap call that proves the token works).</summary>
    public static async Task<string> WhoAmIAsync(CanvasClient client, CancellationToken ct = default)
    {
        var me = await client.GetJsonAsync("/users/self", ct).ConfigureAwait(false);
        return CanvasParser.Str(me, "name") ?? CanvasParser.Str(me, "short_name") ?? "your account";
    }

    /// <summary>Every course with its term: the active ones and, if allowed, the completed ones.</summary>
    public static async Task<List<CanvasCourse>> LoadCoursesAsync(CanvasClient client, CancellationToken ct = default)
    {
        const string q = "per_page=100&include[]=term";
        var items = await client.GetAllAsync("/courses?enrollment_state=active&" + q, ct).ConfigureAwait(false);
        try { items.AddRange(await client.GetAllAsync("/courses?enrollment_state=completed&" + q, ct).ConfigureAwait(false)); }
        catch (CanvasApiException) { /* some instances refuse this filter; the active list is enough */ }
        return CanvasParser.ParseCourses(items);
    }

    /// <summary>One course's start page and modules, with the files linked inside its pages.</summary>
    public static async Task<CanvasCourseData> LoadCourseAsync(CanvasClient client, string courseId, CancellationToken ct = default)
    {
        string c = Uri.EscapeDataString(courseId);
        var modules = await client.GetAllAsync($"/courses/{c}/modules?include[]=items&include[]=content_details&per_page=100", ct)
            .ConfigureAwait(false);

        // Big modules come without their items (or only some): fetch those separately.
        var itemsByModule = new ConcurrentDictionary<string, List<JsonElement>>();
        await ForEachAsync(modules, 4, async m =>
        {
            string mid = CanvasParser.Str(m, "id") ?? "";
            var inline = m.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().ToList()
                : new List<JsonElement>();
            long count = CanvasParser.Long(m, "items_count") ?? inline.Count;
            if (count > inline.Count && mid.Length > 0)
                inline = await client.GetAllAsync($"/courses/{c}/modules/{Uri.EscapeDataString(mid)}/items?include[]=content_details&per_page=100", ct)
                    .ConfigureAwait(false);
            itemsByModule[mid] = inline;
        }, ct).ConfigureAwait(false);

        // Page bodies, for the files and links inside them: all at once, then any missing one by one.
        var pageSlugs = itemsByModule.Values.SelectMany(l => l)
            .Where(i => CanvasParser.Str(i, "type") == "Page")
            .Select(i => CanvasParser.Str(i, "page_url"))
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        var bodies = new ConcurrentDictionary<string, string>();
        if (pageSlugs.Count > 0)
        {
            try
            {
                foreach (var p in await client.GetAllAsync($"/courses/{c}/pages?include[]=body&per_page=100", ct).ConfigureAwait(false))
                    if (CanvasParser.Str(p, "url") is { } slug && CanvasParser.Str(p, "body") is { } body)
                        bodies[slug] = body;
            }
            catch (CanvasApiException) { /* the Pages tab can be hidden too */ }

            await ForEachAsync(pageSlugs.Where(s => !bodies.ContainsKey(s)).ToList(), 6, async slug =>
            {
                try
                {
                    var p = await client.GetJsonAsync($"/courses/{c}/pages/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
                    bodies[slug] = CanvasParser.Str(p, "body") ?? "";
                }
                catch (CanvasApiException) { }
            }, ct).ConfigureAwait(false);
        }

        CanvasFrontPage? front = null;
        try
        {
            var fp = await client.GetJsonAsync($"/courses/{c}/front_page", ct).ConfigureAwait(false);
            front = new CanvasFrontPage
            {
                Title = CanvasParser.Str(fp, "title") is { Length: > 0 } t ? t : "Start page",
                Url = CanvasParser.Str(fp, "html_url") ?? $"{client.BaseUrl}/courses/{c}",
                Links = CanvasHtml.ExtractLinks(CanvasParser.Str(fp, "body"), client.BaseUrl),
                Body = CanvasParser.Str(fp, "body") ?? "",
            };
        }
        catch (CanvasApiException) { /* no start page */ }

        string? Body(string slug) => bodies.TryGetValue(slug, out var b) ? b : null;
        return new CanvasCourseData
        {
            FetchedUtc = DateTime.UtcNow,
            Front = front,
            Modules = modules.Select(m => CanvasParser.ParseModule(m,
                itemsByModule.TryGetValue(CanvasParser.Str(m, "id") ?? "", out var items) ? items : new List<JsonElement>(),
                Body, client.BaseUrl)).ToList(),
        };
    }

    /// <summary>The download facts of a file: what the module item carried, else asked from Canvas (the page's API
    /// endpoint, then <c>/files/:id</c>, then <c>/courses/:cid/files/:id</c>).</summary>
    public static async Task<CanvasFileInfo> ResolveFileAsync(CanvasClient client, string courseId, CanvasItem item, CancellationToken ct = default)
    {
        var f = item.File ?? throw new InvalidOperationException("That is not a file.");
        if (f.Direct is { Url.Length: > 0 } direct) return direct;

        var tries = new List<string>();
        if (!string.IsNullOrEmpty(f.Endpoint) && Uri.TryCreate(f.Endpoint, UriKind.Absolute, out var ep) && client.IsCanvasHost(ep))
            tries.Add(f.Endpoint);
        if (f.Id.Length > 0)
        {
            tries.Add($"/files/{Uri.EscapeDataString(f.Id)}");
            tries.Add($"/courses/{Uri.EscapeDataString(courseId)}/files/{Uri.EscapeDataString(f.Id)}");
        }
        CanvasApiException? last = null;
        foreach (string t in tries)
        {
            try
            {
                var o = await client.GetJsonAsync(t, ct).ConfigureAwait(false);
                if (CanvasParser.ParseFileInfo(o, item.Title) is { } info) return info;
            }
            catch (CanvasApiException ex) { last = ex; }
        }
        throw last ?? new CanvasApiException(404, "Canvas has no download for this file.");
    }

    private static async Task ForEachAsync<T>(IReadOnlyCollection<T> items, int parallel, Func<T, Task> work, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(parallel);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await work(item).ConfigureAwait(false); }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

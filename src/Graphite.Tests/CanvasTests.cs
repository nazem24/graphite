using System.Net;
using System.Text;
using System.Text.Json;
using Graphite.Core.Canvas;
using Xunit;

namespace Graphite.Tests;

public class CanvasParserTests
{
    private static List<JsonElement> Array(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    private static JsonElement Obj(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ParsesCoursesWithTermsAndSkipsNamelessOnes()
    {
        var courses = CanvasParser.ParseCourses(Array("""
            [
              {"id": 27567, "name": "Accounting", "course_code": "ACA", "enrollment_term_id": 264,
               "term": {"name": "HS26", "start_at": "2026-09-14T00:00:00Z", "end_at": "2027-01-31T00:00:00Z"}},
              {"id": 999, "access_restricted_by_date": true},
              {"id": 27480, "name": "Political Economy", "course_code": "CPE", "enrollment_term_id": 264,
               "term": {"name": "HS26"}},
              {"id": 100, "name": "Old course", "enrollment_term_id": 200,
               "term": {"name": "FS25", "start_at": "2025-02-17T00:00:00Z", "end_at": "2025-07-31T00:00:00Z"}}
            ]
            """));
        Assert.Equal(3, courses.Count);
        var aca = courses.Single(c => c.Id == "27567");
        Assert.Equal("ACA", aca.Code);
        Assert.Equal("264", aca.TermId);
        Assert.Equal("HS26", aca.TermName);
        Assert.Equal(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc), aca.TermStartUtc);

        // today is in HS26: that term wins over the older one
        Assert.Equal("264", CanvasParser.PickTerm(courses, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), null));
        // a remembered choice that still exists is kept
        Assert.Equal("200", CanvasParser.PickTerm(courses, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), "200"));
        // a remembered choice that is gone falls back to the running term
        Assert.Equal("264", CanvasParser.PickTerm(courses, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), "1"));
    }

    [Fact]
    public void ParsesModuleItemsOfEveryKind()
    {
        string Body(string slug) => slug == "week-1"
            ? """<p>Slides: <a class="instructure_file_link" href="/courses/1/files/555?wrap=1" data-api-endpoint="https://learning.unisg.ch/api/v1/courses/1/files/555" data-api-returntype="File">Week 1 slides.pdf</a></p>"""
            : "";

        var items = Array("""
            [
              {"id": 1, "type": "SubHeader", "title": "Readings"},
              {"id": 2, "type": "File", "title": "Syllabus", "content_id": 777, "indent": 1,
               "html_url": "https://learning.unisg.ch/courses/1/modules/items/2",
               "content_details": {"url": "https://learning.unisg.ch/files/777/download?download_frd=1",
                                   "display_name": "Syllabus HS26.pdf", "size": 12345, "content-type": "application/pdf",
                                   "updated_at": "2026-09-01T10:00:00Z", "locked_for_user": false}},
              {"id": 3, "type": "Page", "title": "Week 1", "page_url": "week-1"},
              {"id": 4, "type": "Assignment", "title": "Essay", "content_details": {"due_at": "2026-11-01T22:59:00Z", "points_possible": 10, "locked_for_user": true}},
              {"id": 5, "type": "ExternalUrl", "title": "Reading list", "external_url": "https://example.org/list"}
            ]
            """);
        var parsed = items.Select(i => CanvasParser.ParseModuleItem(i, Body, "https://learning.unisg.ch")).ToList();

        Assert.Equal(CanvasItemKind.Header, parsed[0].Kind);

        var file = parsed[1];
        Assert.Equal(CanvasItemKind.File, file.Kind);
        Assert.Equal("file:777", file.Key);
        Assert.Equal(1, file.Indent);
        Assert.Equal("Syllabus HS26.pdf", file.File!.Direct!.Name);
        Assert.Equal(12345, file.File.Direct.Size);

        var page = parsed[2];
        Assert.Equal(CanvasItemKind.Page, page.Kind);
        var linked = Assert.Single(page.Links!);
        Assert.Equal("file:555", linked.Key);
        Assert.Equal("Week 1 slides.pdf", linked.Title);
        Assert.Equal("https://learning.unisg.ch/api/v1/courses/1/files/555", linked.File!.Endpoint);

        var task = parsed[3];
        Assert.Equal(CanvasItemKind.Assignment, task.Kind);
        Assert.True(task.Locked);
        Assert.Equal(new DateTime(2026, 11, 1, 22, 59, 0, DateTimeKind.Utc), task.DueUtc);

        var link = parsed[4];
        Assert.Equal(CanvasItemKind.Link, link.Kind);
        Assert.Equal("link:https://example.org/list", link.Key);
        Assert.Equal("https://example.org/list", link.Url);
    }

    [Fact]
    public void ExtractsFilesAndLinksFromPageHtml()
    {
        const string html = """
            <a href="https://learning.unisg.ch/">Home</a>
            <a href="/courses/1/files/42/download" data-api-returntype="File"><span>Case</span> &amp; notes</a>
            <a href="/courses/1/files/42/download">duplicate</a>
            <a href="https://www.youtube.com/watch?v=x">Lecture video</a>
            <a href="mailto:prof@unisg.ch">Mail</a>
            <a href='https://learning.unisg.ch/courses/1/files/43'>Same host file</a>
            """;
        var links = CanvasHtml.ExtractLinks(html, "https://learning.unisg.ch");
        Assert.Equal(3, links.Count);
        Assert.Equal("file:42", links[0].Key);
        Assert.Equal("Case & notes", links[0].Title);
        Assert.Equal(CanvasItemKind.Link, links[1].Kind);
        Assert.Equal("Lecture video", links[1].Title);
        Assert.Equal("file:43", links[2].Key);
    }

    [Fact]
    public void ReadsCanvasErrorMessages()
    {
        Assert.Equal("Invalid access token.", CanvasParser.ErrorMessage("""{"errors":[{"message":"Invalid access token."}]}"""));
        Assert.Equal("nope", CanvasParser.ErrorMessage("""{"message":"nope"}"""));
        Assert.Null(CanvasParser.ErrorMessage("<html>"));
    }

    [Fact]
    public void FindsTheNextPageInALinkHeader()
    {
        const string link = "<https://x/api/v1/courses?page=1>; rel=\"current\", <https://x/api/v1/courses?page=2&per_page=100>; rel=\"next\"";
        Assert.Equal("https://x/api/v1/courses?page=2&per_page=100", CanvasClient.NextPage(link));
        Assert.Null(CanvasClient.NextPage("<https://x/a>; rel=\"last\""));
    }

    [Fact]
    public void NormalizesTheCanvasAddress()
    {
        Assert.Equal("https://learning.unisg.ch", CanvasClient.NormalizeBase("learning.unisg.ch/").AbsoluteUri.TrimEnd('/'));
        Assert.Equal("https://learning.unisg.ch", CanvasClient.NormalizeBase("https://learning.unisg.ch/courses/1").AbsoluteUri.TrimEnd('/'));
    }
}

public class CanvasNewTests
{
    private static CanvasCourseData Sample() => new()
    {
        Front = new CanvasFrontPage { Links = { new CanvasItem { Key = "link:a", Kind = CanvasItemKind.Link, Title = "A" } } },
        Modules =
        {
            new CanvasModule
            {
                Name = "Week 1",
                Items =
                {
                    new CanvasItem { Key = "item:h", Kind = CanvasItemKind.Header, Title = "Header" },
                    new CanvasItem { Key = "file:1", Kind = CanvasItemKind.File, Title = "Slides" },
                    new CanvasItem
                    {
                        Key = "item:p", Kind = CanvasItemKind.Page, Title = "Overview",
                        Links = new() { new CanvasItem { Key = "file:2", Kind = CanvasItemKind.File, Title = "Case" },
                                  new CanvasItem { Key = "file:1", Kind = CanvasItemKind.File, Title = "Slides again" } },
                    },
                },
            },
        },
    };

    [Fact]
    public void FirstLoadFlagsNothingAndLaterAdditionsAreNew()
    {
        var data = Sample();
        Assert.Empty(CanvasNew.NewKeys(data, null));

        var seen = CanvasNew.AllKeys(data);
        Assert.DoesNotContain("item:h", seen);  // headers are not entries
        Assert.Contains("file:2", seen);         // files inside pages are

        data.Modules[0].Items.Add(new CanvasItem { Key = "file:3", Kind = CanvasItemKind.File, Title = "New slides" });
        Assert.Equal(new[] { "file:3" }, CanvasNew.NewKeys(data, seen));
    }

    [Fact]
    public void PathsDescribeWhereAnEntrySits()
    {
        var flat = CanvasNew.Flatten(Sample()).ToList();
        Assert.Equal("Start page", flat[0].Path);
        Assert.Equal("Week 1", flat.Single(x => x.Item.Title == "Slides").Path);
        Assert.Equal("Week 1 › Overview", flat.Single(x => x.Item.Title == "Case").Path);
    }

    [Fact]
    public void ModuleFilesAreListedOnce()
    {
        var files = CanvasNew.ModuleFiles(Sample().Modules[0]);
        Assert.Equal(new[] { "file:1", "file:2" }, files.Select(f => f.Key));
    }
}

public class CanvasFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "graphite-canvas-" + Guid.NewGuid().ToString("N"));

    public CanvasFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RecognisesRenamedFilesByTheirCanvasId()
    {
        string renamed = Path.Combine(_dir, "C_M1_Management_Accounting.pdf");
        File.WriteAllText(renamed, "pdf");
        CanvasFolderLink.Record(_dir, "27567", "555", renamed);

        var m = CanvasFolderLink.Load(_dir);
        Assert.Equal("27567", m.CourseId);
        Assert.Equal(renamed, CanvasFolderLink.FindLocal(_dir, m, "555"));

        var (path, exists) = CanvasFolderLink.Place(_dir, m, "555", "Week 1 ACA M1-M3 Management Accounting.pdf", 3);
        Assert.True(exists);
        Assert.Equal(renamed, path);
    }

    [Fact]
    public void AdoptsASameNameSameSizeFileAndAvoidsOverwritingOthers()
    {
        File.WriteAllText(Path.Combine(_dir, "Slides.pdf"), "12345");
        var m = CanvasFolderLink.Load(_dir);

        var same = CanvasFolderLink.Place(_dir, m, "1", "Slides.pdf", 5);
        Assert.True(same.Exists);

        var different = CanvasFolderLink.Place(_dir, m, "2", "Slides.pdf", 99);
        Assert.False(different.Exists);
        Assert.Equal(Path.Combine(_dir, "Slides (2).pdf"), different.Path);
    }

    [Fact]
    public void CleansFileNames()
    {
        Assert.Equal("Week 1_ Slides_.pdf", CanvasNames.SafeFileName("Week 1: Slides?.pdf"));
        Assert.Equal("file", CanvasNames.SafeFileName("  "));
        Assert.Equal("pdf", CanvasNames.Extension("a.PDF"));
    }

    [Fact]
    public void CacheRoundTripsCoursesAndSeenSets()
    {
        var cache = new CanvasCache(_dir);
        Assert.Null(cache.LoadSeen("1"));
        cache.SaveCourses(new List<CanvasCourse> { new() { Id = "1", Name = "ACA", TermId = "264" } });
        cache.SaveCourse("1", new CanvasCourseData
        {
            FetchedUtc = DateTime.UtcNow,
            Modules = { new CanvasModule { Name = "M", Items = { new CanvasItem { Key = "file:9", Kind = CanvasItemKind.File } } } },
        });
        cache.SaveSeen("1", new[] { "file:9" });

        Assert.Equal("ACA", cache.LoadCourses()!.Courses.Single().Name);
        Assert.Equal(CanvasItemKind.File, cache.LoadCourse("1")!.Modules[0].Items[0].Kind);
        Assert.Contains("file:9", cache.LoadSeen("1")!);

        cache.ClearStructure();
        Assert.Null(cache.LoadCourse("1"));
        Assert.Null(cache.LoadCourses());
        Assert.NotNull(cache.LoadSeen("1"));
    }
}

public class CanvasClientTests
{
    /// <summary>Answers requests from a script and records what was sent.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, string? link = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (link != null) r.Headers.TryAddWithoutValidation("Link", link);
        return r;
    }

    [Fact]
    public async Task FollowsPagesAndSendsAUserAgent()
    {
        var fake = new FakeHandler();
        fake.Respond = req => req.RequestUri!.Query.Contains("page=2")
            ? Json("[{\"id\":2}]")
            : Json("[{\"id\":1}]", "<https://learning.unisg.ch/api/v1/courses?page=2&per_page=100>; rel=\"next\"");
        using var client = new CanvasClient("https://learning.unisg.ch", "secret", fake);

        var all = await client.GetAllAsync("/courses?per_page=100");

        Assert.Equal(2, all.Count);
        Assert.All(fake.Requests, r => Assert.Contains("Mozilla", string.Join(" ", r.Headers.GetValues("User-Agent"))));
        Assert.All(fake.Requests, r => Assert.Equal("Bearer", r.Headers.Authorization?.Scheme));
    }

    [Fact]
    public async Task NeverSendsTheTokenToAnotherHost()
    {
        var fake = new FakeHandler();
        fake.Respond = req =>
        {
            if (req.RequestUri!.Host == "learning.unisg.ch")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://files.instructure-uploads.example/abc?sig=1");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        };
        using var client = new CanvasClient("https://learning.unisg.ch", "secret", fake);
        using var ms = new MemoryStream();

        await client.DownloadAsync("https://learning.unisg.ch/files/1/download", ms);

        Assert.Equal(3, ms.Length);
        Assert.Equal(2, fake.Requests.Count);
        Assert.NotNull(fake.Requests[0].Headers.Authorization);
        Assert.Null(fake.Requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task ReportsCanvasErrors()
    {
        var fake = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"errors\":[{\"message\":\"Invalid access token.\"}]}"),
            },
        };
        using var client = new CanvasClient("https://learning.unisg.ch", "bad", fake);
        var ex = await Assert.ThrowsAsync<CanvasApiException>(() => client.GetJsonAsync("/users/self"));
        Assert.Equal(401, ex.Status);
        Assert.Equal("Invalid access token.", ex.Message);
    }
}

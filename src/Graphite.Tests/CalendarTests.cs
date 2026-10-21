using System.Text.Json;
using Graphite.Core.Canvas;
using Xunit;

namespace Graphite.Tests;

public class IcsCalendarTests
{
    private static string Ics(params string[] eventLines) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nX-WR-CALNAME:Uni\r\n" + string.Join("\r\n", eventLines) + "\r\nEND:VCALENDAR\r\n";

    private static string Event(params string[] lines) => "BEGIN:VEVENT\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT";

    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime To = new(2026, 11, 30);

    [Fact]
    public void ReadsANameAndASingleTimedEvent()
    {
        var cal = IcsParser.Parse(Ics(Event(
            "UID:a1", "DTSTART:20261020T101500", "DTEND:20261020T114500", "SUMMARY:International Affairs", "LOCATION:Room 09-010")));
        Assert.Equal("Uni", cal.Name);

        var entry = Assert.Single(cal.Expand(From, To));
        Assert.Equal(CalendarKind.Lecture, entry.Kind);
        Assert.Equal("International Affairs", entry.Title);
        Assert.Equal("Room 09-010", entry.Location);
        Assert.Equal(new DateTime(2026, 10, 20, 10, 15, 0), entry.Start);
        Assert.Equal(new DateTime(2026, 10, 20, 11, 45, 0), entry.End);
        Assert.False(entry.AllDay);
    }

    [Fact]
    public void UnfoldsLongLinesAndUnescapesText()
    {
        var cal = IcsParser.Parse(Ics(Event(
            "UID:a2", "DTSTART:20261020T101500", "DTEND:20261020T114500", "SUMMARY:Marketing\\, Digital", " Ventures")));
        Assert.Equal("Marketing, DigitalVentures", Assert.Single(cal.Expand(From, To)).Title);
    }

    [Fact]
    public void WeeklyRuleWithWeekdaysAndCount()
    {
        var cal = IcsParser.Parse(Ics(Event(
            "UID:w1", "DTSTART:20261006T101500", "DTEND:20261006T114500", "SUMMARY:Finance",
            "RRULE:FREQ=WEEKLY;BYDAY=TU,TH;COUNT=4")));
        var days = cal.Expand(From, To).Select(e => e.Start.Date).OrderBy(d => d).ToList();
        Assert.Equal(new[] { new DateTime(2026, 10, 6), new DateTime(2026, 10, 8), new DateTime(2026, 10, 13), new DateTime(2026, 10, 15) }, days);
    }

    [Fact]
    public void ExcludedDatesAndChangedOccurrences()
    {
        var cal = IcsParser.Parse(Ics(
            Event("UID:w2", "DTSTART:20261006T101500", "DTEND:20261006T114500", "SUMMARY:Finance",
                "RRULE:FREQ=WEEKLY;COUNT=4", "EXDATE:20261013T101500"),
            Event("UID:w2", "RECURRENCE-ID:20261020T101500", "DTSTART:20261020T141500", "DTEND:20261020T154500", "SUMMARY:Finance (moved)")));
        var found = cal.Expand(From, To).OrderBy(e => e.Start).ToList();
        Assert.Equal(3, found.Count);
        Assert.Equal(new DateTime(2026, 10, 6, 10, 15, 0), found[0].Start);
        Assert.Equal(new DateTime(2026, 10, 20, 14, 15, 0), found[1].Start);   // the moved one, not 10:15
        Assert.Equal("Finance (moved)", found[1].Title);
        Assert.Equal(new DateTime(2026, 10, 27, 10, 15, 0), found[2].Start);
    }

    [Fact]
    public void AllDayEventsSpanTheirDays()
    {
        var cal = IcsParser.Parse(Ics(Event("UID:d1", "DTSTART;VALUE=DATE:20261105", "DTEND;VALUE=DATE:20261107", "SUMMARY:Case week")));
        var entry = Assert.Single(cal.Expand(From, To));
        Assert.True(entry.AllDay);
        Assert.Equal(new DateTime(2026, 11, 5), entry.Start);
        Assert.Equal(new DateTime(2026, 11, 7), entry.End);
    }

    [Fact]
    public void ExamsAreRecognisedAndCancelledEventsSkipped()
    {
        var cal = IcsParser.Parse(Ics(
            Event("UID:e1", "DTSTART:20261027T121500", "DTEND:20261027T134500", "SUMMARY:Midterm Statistics II"),
            Event("UID:e2", "DTSTART:20261028T121500", "DTEND:20261028T134500", "SUMMARY:Example class"),
            Event("UID:e3", "DTSTART:20261029T121500", "DTEND:20261029T134500", "SUMMARY:Cancelled thing", "STATUS:CANCELLED")));
        var found = cal.Expand(From, To).OrderBy(e => e.Start).ToList();
        Assert.Equal(2, found.Count);
        Assert.Equal(CalendarKind.Exam, found[0].Kind);
        Assert.Equal(CalendarKind.Lecture, found[1].Kind);   // "Example" is not an exam
    }

    [Fact]
    public void AllDayAndRegistrationDatesAreNotLecturesOrExams()
    {
        var cal = IcsParser.Parse(Ics(
            Event("UID:a1", "DTSTART;VALUE=DATE:20261022", "DTEND;VALUE=DATE:20261023", "SUMMARY:Semestereinschreibung für FS"),
            Event("UID:a2", "DTSTART;VALUE=DATE:20261022", "DTEND;VALUE=DATE:20261023", "SUMMARY:BuHa Jan: Prüfungsanmeldung in Compass"),
            Event("UID:a3", "DTSTART;VALUE=DATE:20261022", "DTEND;VALUE=DATE:20261023", "SUMMARY:Zentral: kostenlose Prüfungsabmeldephase"),
            Event("UID:a4", "DTSTART:20261022T101500", "DTEND:20261022T114500", "SUMMARY:Völkerrecht: Übungen"),
            Event("UID:a5", "DTSTART:20261022T121500", "DTEND:20261022T134500", "SUMMARY:Klausur Völkerrecht")));
        var found = cal.Expand(From, To).OrderBy(e => e.Title, StringComparer.Ordinal).ToDictionary(e => e.Title);
        Assert.Equal(CalendarKind.Notice, found["Semestereinschreibung für FS"].Kind);
        Assert.Equal(CalendarKind.Notice, found["BuHa Jan: Prüfungsanmeldung in Compass"].Kind);
        Assert.Equal(CalendarKind.Notice, found["Zentral: kostenlose Prüfungsabmeldephase"].Kind);
        Assert.Equal(CalendarKind.Lecture, found["Völkerrecht: Übungen"].Kind);
        Assert.Equal(CalendarKind.Exam, found["Klausur Völkerrecht"].Kind);
    }

    [Fact]
    public void UrlsAreNormalisedAndNonWebAddressesRejected()
    {
        Assert.StartsWith("https://example.com/", IcsParser.NormalizeUrl("webcal://example.com/cal.ics"));
        Assert.Throws<ArgumentException>(() => IcsParser.NormalizeUrl("not an address"));
        Assert.Throws<ArgumentException>(() => IcsParser.NormalizeUrl("file:///c:/x.ics"));
    }
}

public class CalendarCanvasTests
{
    private static readonly CanvasCourse Marketing = new() { Id = "1", Name = "Marketing (Digital Ventures and E-Commerce)" };
    private static readonly CanvasCourse Affairs = new() { Id = "2", Name = "International Affairs: Theory and Practice" };

    [Fact]
    public void MatchesLecturesToCoursesByTitle()
    {
        var courses = new[] { Marketing, Affairs };
        Assert.Same(Marketing, CalendarCourseMatcher.Match("Marketing", courses));
        Assert.Same(Affairs, CalendarCourseMatcher.Match("Intl. Affairs", courses));
        Assert.Null(CalendarCourseMatcher.Match("Lunch", courses));
    }

    [Fact]
    public void ParsesDeadlinesFromCalendarEvents()
    {
        var items = JsonDocument.Parse("""
            [
              {"id": 5, "title": "Essay outline", "context_code": "course_2", "html_url": "https://x.example/a/9",
               "start_at": "2026-10-20T21:59:00Z",
               "assignment": {"id": 9, "name": "Essay outline", "due_at": "2026-10-20T21:59:00Z"}},
              {"id": 6, "title": "No date at all", "context_code": "course_2"}
            ]
            """).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        var courses = new Dictionary<string, CanvasCourse> { ["course_2"] = Affairs };

        var entry = Assert.Single(CanvasCalendarParser.ParseDeadlines(items, courses));
        Assert.Equal(CalendarKind.Deadline, entry.Kind);
        Assert.Equal("Essay outline", entry.Title);
        Assert.Equal("2", entry.CourseId);
        Assert.Equal(Affairs.Name, entry.CourseName);
        Assert.Equal("https://x.example/a/9", entry.Url);
        Assert.Equal(new DateTime(2026, 10, 20, 21, 59, 0, DateTimeKind.Utc).ToLocalTime(), entry.Start);
    }
}

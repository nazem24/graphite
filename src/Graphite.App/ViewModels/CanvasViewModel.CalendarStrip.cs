using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using Graphite.App.Services;
using Graphite.Core.Canvas;

namespace Graphite.App.ViewModels;

/// <summary>
/// The strip at the top of the overview. When nothing is new (and the person switched it on in Settings) it shows the calendar instead, in the same cards as
/// "New since you last looked": what is on today, the next due dates and the current all-day entries (semester dates,
/// registration phases and the like). Follows the switches of the calendar page, except that the all-day box is always there.
/// </summary>
public sealed partial class CanvasViewModel
{
    /// <summary>Rows of the due-date and all-day cards before they say "+ n more". The Today card shows the whole day.</summary>
    private const int StripRows = 5;

    public ObservableCollection<CalendarStripCard> CalendarStrip { get; } = new();

    [ObservableProperty] private bool hasCalendarStrip;

    private string _stripSignature = "";

    private void BuildCalendarStrip()
    {
        var cards = new List<CalendarStripCard>();
        bool wanted = CanvasSettings.CalendarOverviewStrip && Page == CanvasPage.Overview && !HasNewPreview && _calendarDiskLoaded && IsConnected
                      && (_deadlines.Count > 0 || _ics.Count > 0);
        if (wanted)
        {
            DateTime now = DateTime.Now, today = DateTime.Today;
            var all = CollectEntries(today, today.AddDays(60));

            if (ShowLectures || ShowDueDates)
            {
                var rows = all.Where(e => !e.Entry.AllDay && e.Start.Date == today).ToList();
                foreach (var e in rows) e.StripText = e.TimeText;
                cards.Add(new CalendarStripCard("Today", today.ToString("dddd d MMMM", CultureInfo.CurrentCulture), "Nothing on today.", rows, ""));
            }
            if (ShowDueDates)
            {
                var rows = all.Where(e => e.IsDeadline && e.Start >= now).ToList();
                foreach (var e in rows) e.StripText = e.WhenText;
                cards.Add(Card("Upcoming due dates", rows.Count > 0 ? rows.Count + " open" : "", "No due dates ahead.", rows));
            }
            if (_ics.Count > 0)
            {
                // always shown, whatever the all-day switch of the calendar page says: that switch is for the grid
                DateTime horizon = today.AddDays(8);
                var termCourses = CoursesInTerm().ToList();
                var rows = _ics.Values
                    .SelectMany(c => c.Expand(today, horizon))
                    .Where(e => e.AllDay && e.Start.Date < horizon && (e.End.Date > today || e.Start.Date == today))
                    .Select(e => MakeVm(e, termCourses))
                    .OrderBy(x => x.Start)
                    .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                foreach (var e in rows)
                {
                    DateTime last = e.End.Date > e.Start.Date ? e.End.Date.AddDays(-1) : e.Start.Date;
                    e.StripText = e.Start.Date > today ? e.DayText
                        : last > today ? "Until " + last.ToString("ddd d MMM", CultureInfo.CurrentCulture)
                        : "Today";
                }
                cards.Add(Card("All-day", "Next 7 days", "Nothing all-day right now.", rows));
            }
        }

        string signature = string.Join("¦", cards.Select(c =>
            c.Title + "|" + c.Subtitle + "|" + c.MoreText + "|" + string.Join(";", c.Rows.Select(r => r.Entry.Id + "@" + r.StripText))));
        if (signature == _stripSignature) return;
        _stripSignature = signature;
        CalendarStrip.Clear();
        foreach (var card in cards) CalendarStrip.Add(card);
        HasCalendarStrip = cards.Count > 0;
    }

    private static CalendarStripCard Card(string title, string subtitle, string empty, List<CalendarEntryVm> rows)
    {
        if (rows.Count <= StripRows) return new CalendarStripCard(title, subtitle, empty, rows, "");
        return new CalendarStripCard(title, subtitle, empty, rows.Take(StripRows - 1).ToList(), $"+ {rows.Count - (StripRows - 1)} more");
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.Core.Canvas;

namespace Graphite.App.ViewModels;

/// <summary>How the Canvas calendar shows its days.</summary>
public enum CalendarViewMode { Month, Week, Agenda }

/// <summary>One lecture, exam or deadline on the calendar, with the colour of its course.</summary>
public sealed class CalendarEntryVm
{
    public CalendarEntryVm(CalendarEntry entry, Brush tint)
    {
        Entry = entry;
        Tint = tint;
    }

    public CalendarEntry Entry { get; }

    /// <summary>The bar colour: the course's colour (grey when no course could be told).</summary>
    public Brush Tint { get; }

    public string Title => Entry.Title;
    public CalendarKind Kind => Entry.Kind;
    public bool IsDeadline => Entry.Kind == CalendarKind.Deadline;
    public bool IsExam => Entry.Kind == CalendarKind.Exam;
    public bool IsLecture => Entry.Kind == CalendarKind.Lecture;
    public bool IsNotice => Entry.Kind == CalendarKind.Notice;
    public bool HasUrl => Entry.Url.Length > 0;
    public DateTime Start => Entry.Start;
    public DateTime End => Entry.End;

    public string KindLabel => Entry.Kind switch
    {
        CalendarKind.Deadline => "DUE",
        CalendarKind.Exam => "EXAM",
        CalendarKind.Notice => "EVENT",
        _ => "LECTURE",
    };

    private static string Clock(DateTime t) => t.ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"10:15", or "All day".</summary>
    public string TimeText => Entry.AllDay ? "All day" : Clock(Entry.Start);

    /// <summary>"Today", "Tomorrow" or "Fri 23 Oct".</summary>
    public string DayText => Entry.Start.Date == DateTime.Today ? "Today"
        : Entry.Start.Date == DateTime.Today.AddDays(1) ? "Tomorrow"
        : Entry.Start.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    /// <summary>"Tomorrow · 12:00" (just the day for an all-day entry).</summary>
    public string WhenText => Entry.AllDay ? DayText : DayText + " · " + Clock(Entry.Start);

    /// <summary>The first column when the entry is a row of a card on the overview (set by whoever builds the card).</summary>
    public string StripText { get; set; } = "";

    /// <summary>"08:15 – 09:45" (just the time for a deadline).</summary>
    public string RangeText => Entry.AllDay ? "All day" : Entry.End > Entry.Start ? Clock(Entry.Start) + " – " + Clock(Entry.End) : Clock(Entry.Start);

    /// <summary>The title up to its first bracket or colon: "Marketing (Digital Ventures…)" → "Marketing".</summary>
    public string ShortTitle
    {
        get
        {
            string t = Entry.Title;
            int cut = t.IndexOfAny(new[] { '(', ':' });
            return (cut > 2 ? t[..cut] : t).Trim();
        }
    }

    /// <summary>The text of a month cell's chip: "Due: Essay outline", "Finance 10:15".</summary>
    public string ChipText => IsDeadline ? "Due: " + Entry.Title : Entry.AllDay ? Entry.Title : ShortTitle + " " + Clock(Entry.Start);

    /// <summary>The text of a deadline's chip above the week grid: "Essay outline 23:59".</summary>
    public string WeekChipText => Entry.AllDay ? Entry.Title : Entry.Title + " " + Clock(Entry.Start);

    /// <summary>The second line in lists: the course of a deadline, the room of a lecture.</summary>
    public string Detail => IsDeadline
        ? (Entry.CourseName.Length > 0 ? Entry.CourseName : "Canvas")
        : Entry.Location;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The second line of a block in the week grid: "10:15 – 11:45 · Room 09-010".</summary>
    public string BlockDetail => Entry.Location.Length > 0 ? RangeText + " · " + Entry.Location : RangeText;

    // where the block sits in the week grid (set when the week is built)
    public double BlockTop { get; set; }
    public double BlockHeight { get; set; }
    public int Col { get; set; }
    public int Cols { get; set; } = 1;
}

/// <summary>One day: a cell of the month grid, a column of the week, or a number of the little calendar.</summary>
public sealed partial class CalendarDayVm : ObservableObject
{
    public CalendarDayVm(DateTime date, bool inMonth)
    {
        Date = date;
        InMonth = inMonth;
        IsToday = date.Date == DateTime.Today;
    }

    public DateTime Date { get; }
    public int DayNumber => Date.Day;
    public bool InMonth { get; }
    public bool IsToday { get; }

    /// <summary>"MON".</summary>
    public string WeekdayLabel => Date.ToString("ddd", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);

    [ObservableProperty] private bool isSelected;

    /// <summary>Month cell: the first three entries. Week column: the deadlines and all-day entries above the grid.</summary>
    public ObservableCollection<CalendarEntryVm> Chips { get; } = new();
    public bool HasChips => Chips.Count > 0;

    /// <summary>Week column: the timed entries drawn in the grid.</summary>
    public ObservableCollection<CalendarEntryVm> Timed { get; } = new();

    public int MoreCount { get; set; }
    public bool HasMore => MoreCount > 0;
    public string MoreText => "+" + MoreCount + " more";

    // little calendar
    public bool HasLectureDot { get; set; }
    public bool HasDueDot { get; set; }
    public bool HasExamDot { get; set; }
}

/// <summary>A line of the week grid's hour column.</summary>
public sealed record CalendarHourVm(string Label);

/// <summary>A day of the agenda with what happens on it.</summary>
public sealed class CalendarAgendaGroup
{
    public CalendarAgendaGroup(string heading, string date, IReadOnlyList<CalendarEntryVm> items)
    {
        Heading = heading;
        Date = date;
        Items = items;
    }

    public string Heading { get; }
    public string Date { get; }
    public IReadOnlyList<CalendarEntryVm> Items { get; }
}

/// <summary>A linked calendar in the month view's side card.</summary>
public sealed record CalendarSourceRow(string Name, string Status);

/// <summary>One card of the calendar strip on the overview: a title and a few entries.</summary>
public sealed class CalendarStripCard
{
    public CalendarStripCard(string title, string subtitle, string emptyText, List<CalendarEntryVm> rows, string more)
    {
        Title = title;
        Subtitle = subtitle;
        EmptyText = emptyText;
        Rows = rows;
        MoreText = more;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string EmptyText { get; }
    public string MoreText { get; }
    public List<CalendarEntryVm> Rows { get; }
    public bool IsEmpty => Rows.Count == 0;
    public bool HasMore => MoreText.Length > 0;
}

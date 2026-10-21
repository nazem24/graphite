using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core.Canvas;
using Graphite.Core.Library;

namespace Graphite.App.ViewModels;

/// <summary>
/// The Calendar page of the Canvas space: deadlines from Canvas (assignments and quizzes with a due date, one request
/// per ten courses of the term) together with lectures and exams from the person's own calendar links (.ics), in a
/// month, a week and an agenda view. The bar of an entry has the colour of its course (deadlines know their course;
/// a lecture is matched to a course by its title). Nothing is written anywhere: Graphite only reads.
///
/// Like the rest of the space it stays quiet: deadlines are asked for when the space opens and they are older than
/// half an hour, calendar links only while the Calendar page is open, and both come from the disk cache first.
/// </summary>
public sealed partial class CanvasViewModel
{
    /// <summary>Pixels per hour in the week grid.</summary>
    private const double HourHeight = 58;

    private static readonly TimeSpan CalendarFresh = TimeSpan.FromMinutes(30);
    private static readonly Brush NeutralTint = FrozenBrush(0x8A, 0x93, 0xA6);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private List<CalendarEntry> _deadlines = new();
    private string _deadlinesKey = "";
    private DateTime _deadlinesFetchedUtc;
    private readonly Dictionary<string, IcsCalendar> _ics = new();
    private readonly Dictionary<string, string> _icsErrors = new();
    private DateTime _icsFetchedUtc;
    private string? _calendarError;
    private bool _calendarBusy;
    private bool _calendarDiskLoaded;
    private DateTime _lastCalendarAttemptUtc;
    private string _calendarSignature = "";
    private List<CalendarEntryVm> _entries = new();
    private int _weekStartHour = 8;
    private DateTime _weekStart;
    private string _agendaSubtitle = "";

    // ------------------------------------------------------------- state for the views

    public BulkObservableCollection<CalendarDayVm> MonthDays { get; } = new();
    public BulkObservableCollection<CalendarDayVm> WeekDays { get; } = new();
    public BulkObservableCollection<CalendarHourVm> WeekHours { get; } = new();
    public BulkObservableCollection<CalendarAgendaGroup> AgendaGroups { get; } = new();
    public BulkObservableCollection<CalendarDayVm> MiniDays { get; } = new();
    public BulkObservableCollection<CalendarEntryVm> DayEntries { get; } = new();
    public BulkObservableCollection<CalendarSourceRow> CalendarSourceRows { get; } = new();

    public bool IsCalendarPage => Page == CanvasPage.Calendar;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonthMode))]
    [NotifyPropertyChangedFor(nameof(IsWeekMode))]
    [NotifyPropertyChangedFor(nameof(IsAgendaMode))]
    [NotifyPropertyChangedFor(nameof(ShowCalendarNav))]
    private CalendarViewMode calMode = ParseMode(CanvasSettings.CalendarOpenMode);

    public bool IsMonthMode => CalMode == CalendarViewMode.Month;
    public bool IsWeekMode => CalMode == CalendarViewMode.Week;
    public bool IsAgendaMode => CalMode == CalendarViewMode.Agenda;

    /// <summary>The arrows and "Today" are for month and week; the agenda always starts today.</summary>
    public bool ShowCalendarNav => CalMode != CalendarViewMode.Agenda;

    /// <summary>The month or week on show is the one containing this day.</summary>
    [ObservableProperty] private DateTime focusDate = DateTime.Today;

    /// <summary>The day whose entries the side panel lists (month view).</summary>
    [ObservableProperty] private DateTime selectedDate = DateTime.Today;

    [ObservableProperty] private string dayPanelLabel = "TODAY";
    [ObservableProperty] private string dayPanelTitle = "";
    [ObservableProperty] private bool dayPanelEmpty;
    [ObservableProperty] private double weekHeight = 10 * HourHeight;
    [ObservableProperty] private bool showNowLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowLineMargin))]
    private double nowLineTop;

    /// <summary>Puts the "now" line at its height in the week grid.</summary>
    public System.Windows.Thickness NowLineMargin => new(0, NowLineTop, 0, 0);
    [ObservableProperty] private string miniTitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDueSoon))]
    [NotifyPropertyChangedFor(nameof(DueSoonText))]
    private int dueSoonCount;

    /// <summary>The count next to Calendar in the sidebar: deadlines from now to the end of this week.</summary>
    public bool HasDueSoon => DueSoonCount > 0;
    public string DueSoonText => DueSoonCount + " due";

    [ObservableProperty] private string calendarStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCalendarMessage))]
    private string calendarMessage = "";
    public bool HasCalendarMessage => CalendarMessage.Length > 0;

    [ObservableProperty] private bool hasCalendarSources;

    // agenda filter
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgendaAll))]
    [NotifyPropertyChangedFor(nameof(AgendaDeadlines))]
    [NotifyPropertyChangedFor(nameof(AgendaLectures))]
    [NotifyPropertyChangedFor(nameof(AgendaExams))]
    private string agendaFilter = "All";

    public bool AgendaAll => AgendaFilter == "All";
    public bool AgendaDeadlines => AgendaFilter == "Deadline";
    public bool AgendaLectures => AgendaFilter == "Lecture";
    public bool AgendaExams => AgendaFilter == "Exam";

    // "Show on the calendar"
    public bool ShowDueDates
    {
        get => CanvasSettings.CalendarShowDue;
        set
        {
            CanvasSettings.SetCalendarShowDue(value);
            OnPropertyChanged();
        }
    }

    public bool ShowLectures
    {
        get => CanvasSettings.CalendarShowLectures;
        set
        {
            CanvasSettings.SetCalendarShowLectures(value);
            OnPropertyChanged();
        }
    }

    public bool ShowAllDay
    {
        get => CanvasSettings.CalendarShowAllDay;
        set
        {
            CanvasSettings.SetCalendarShowAllDay(value);
            OnPropertyChanged();
        }
    }

    private static CalendarViewMode ParseMode(string? name) =>
        Enum.TryParse<CalendarViewMode>(name, out var mode) ? mode : CalendarViewMode.Month;

    private static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>The first day of the month grid and how many weeks it has.</summary>
    private static (DateTime Start, int Weeks) MonthGrid(DateTime firstOfMonth)
    {
        int lead = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        int days = DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month);
        return (firstOfMonth.AddDays(-lead), (int)Math.Ceiling((lead + days) / 7.0));
    }

    // ------------------------------------------------------------- loading

    /// <summary>The deadlines and calendar copies of the last time, from disk (cheap; done once).</summary>
    private void LoadCalendarFromDisk()
    {
        if (_calendarDiskLoaded) return;
        _calendarDiskLoaded = true;
        if (_cache.LoadDeadlines() is { } saved)
        {
            _deadlines = saved.Entries;
            _deadlinesKey = saved.Key;
            _deadlinesFetchedUtc = saved.FetchedUtc;
        }
        LoadIcsFromDisk();
        _calendarSignature = CalendarSignature();
        UpdateDueSoon();
        if (Page == CanvasPage.Overview) BuildCalendarStrip();
    }

    private void LoadIcsFromDisk()
    {
        var sources = CanvasSettings.CalendarSources();
        var keep = sources.Select(s => s.Id).ToHashSet();
        foreach (string id in _ics.Keys.Where(key => !keep.Contains(key)).ToList()) _ics.Remove(id);
        foreach (string id in _icsErrors.Keys.Where(key => !keep.Contains(key)).ToList()) _icsErrors.Remove(id);
        foreach (var source in sources)
        {
            if (_ics.ContainsKey(source.Id)) continue;
            string name = CanvasSettings.CalendarCacheName(source.Id);
            if (_cache.LoadText(name) is not { } text) continue;
            try { _ics[source.Id] = IcsParser.Parse(text); }
            catch (Exception ex) { App.LogError("Reading a calendar failed", ex); continue; }
            if (_cache.TextTimeUtc(name) is { } finished && finished > _icsFetchedUtc) _icsFetchedUtc = finished;
        }
    }

    private string CurrentDeadlineKey() =>
        CurrentTermId + "|" + string.Join(",", CoursesInTerm().Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));

    private bool DeadlinesStale() =>
        _allCourses.Count > 0 && (DateTime.UtcNow - _deadlinesFetchedUtc > CalendarFresh || _deadlinesKey != CurrentDeadlineKey());

    private bool IcsStale(IReadOnlyList<CanvasSettings.CalendarSource> sources) =>
        sources.Count > 0 && (sources.Any(s => !_ics.ContainsKey(s.Id)) || DateTime.UtcNow - _icsFetchedUtc > CalendarFresh);

    /// <summary>Called after the course list or the term changed: deadlines of another term are asked for.</summary>
    private void CalendarCoursesChanged()
    {
        if (!_calendarDiskLoaded) return;
        if (_active && IsConnected && DeadlinesStale()) _ = RefreshCalendarAsync(force: false);
    }

    /// <summary>Ask Canvas for the deadlines and fetch the calendar links, as far as they are old (or all of it when
    /// forced). Quiet when everything is fresh, and at most one attempt every two minutes.</summary>
    private async Task RefreshCalendarAsync(bool force)
    {
        if (_calendarBusy) return;
        var sources = CanvasSettings.CalendarSources();
        bool keyChanged = IsConnected && _allCourses.Count > 0 && _deadlinesKey != CurrentDeadlineKey();
        bool wantDue = IsConnected && _allCourses.Count > 0 && (force || DeadlinesStale());
        bool wantIcs = sources.Count > 0 && (force || ((Page == CanvasPage.Calendar || Page == CanvasPage.Overview) && IcsStale(sources)));
        if (!wantDue && !wantIcs) return;
        if (!force && !keyChanged && DateTime.UtcNow - _lastCalendarAttemptUtc < TimeSpan.FromMinutes(2)) return;

        _calendarBusy = true;
        _lastCalendarAttemptUtc = DateTime.UtcNow;
        UpdateCalendarStatus();
        try
        {
            if (wantDue) await LoadDeadlinesAsync();
            if (wantIcs) await LoadIcsAsync(sources);
        }
        finally
        {
            _calendarBusy = false;
            UpdateDueSoon();
            UpdateCalendarStatus();
            if (Page == CanvasPage.Calendar) RebuildCalendar();
            else if (Page == CanvasPage.Overview) BuildCalendarStrip();
        }
    }

    private async Task LoadDeadlinesAsync()
    {
        if (Client() is not { } client) return;
        var courses = CoursesInTerm().ToList();
        string key = CurrentDeadlineKey();
        if (courses.Count == 0)
        {
            _deadlines = new List<CalendarEntry>();
            _deadlinesKey = key;
            _deadlinesFetchedUtc = DateTime.UtcNow;
            return;
        }

        DateTime today = DateTime.UtcNow.Date;
        try
        {
            var list = await CanvasFetcher.LoadDeadlinesAsync(client, courses, today.AddDays(-150), today.AddDays(300), CancellationToken.None);
            _deadlines = list;
            _deadlinesKey = key;
            _deadlinesFetchedUtc = DateTime.UtcNow;
            _calendarError = null;
            var snapshot = new CanvasCache.DeadlineList { FetchedUtc = _deadlinesFetchedUtc, Key = key, Entries = list };
            await Task.Run(() => _cache.SaveDeadlines(snapshot));
        }
        catch (Exception ex) when (ex is CanvasApiException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            HandleAuth(ex);
            // a rejected token or no connection already has its banner; anything else is told on the page
            _calendarError = (ex is CanvasApiException { Status: 401 } or HttpRequestException) ? null : "Deadlines: " + Describe(ex);
        }
    }

    private async Task LoadIcsAsync(IReadOnlyList<CanvasSettings.CalendarSource> sources)
    {
        bool any = false;
        foreach (var source in sources)
        {
            try
            {
                string text = await IcsFetcher.DownloadAsync(source.Url);
                var calendar = IcsParser.Parse(text);
                _ics[source.Id] = calendar;
                _icsErrors.Remove(source.Id);
                string name = CanvasSettings.CalendarCacheName(source.Id);
                await Task.Run(() => _cache.SaveText(name, text));
                any = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or ArgumentException or IOException or TaskCanceledException)
            {
                _icsErrors[source.Id] = source.Name + ": " + (ex is TaskCanceledException ? "no answer in time." : ex.Message);
            }
        }
        if (any) _icsFetchedUtc = DateTime.UtcNow;
    }

    // ------------------------------------------------------------- settings and timers

    private string CalendarSignature() =>
        CanvasSettings.CalendarShowDue + "|" + CanvasSettings.CalendarShowLectures + "|" + CanvasSettings.CalendarShowAllDay + "|" + CanvasSettings.CalendarOverviewStrip + "|"
        + string.Join(",", CanvasSettings.CalendarSources().Select(s => s.Id));

    private void OnCalendarSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowDueDates));
        OnPropertyChanged(nameof(ShowLectures));
        OnPropertyChanged(nameof(ShowAllDay));

        string signature = CalendarSignature();
        if (signature == _calendarSignature) return;
        _calendarSignature = signature;
        if (!_calendarDiskLoaded) return;

        LoadIcsFromDisk();
        UpdateCalendarStatus();
        if (Page == CanvasPage.Calendar) RebuildCalendar();
        else if (Page == CanvasPage.Overview) BuildCalendarStrip();
        if (_active) _ = RefreshCalendarAsync(force: false);
    }

    /// <summary>Every half minute while the space is open: the "now" line moves, "synced … ago" and the due count follow.</summary>
    private void TickCalendar()
    {
        if (!_calendarDiskLoaded) return;
        UpdateDueSoon();
        if (Page == CanvasPage.Overview) BuildCalendarStrip();   // the day or the next due date may have moved on
        if (Page != CanvasPage.Calendar) return;
        UpdateCalendarStatus();
        if (CalMode == CalendarViewMode.Week) UpdateNowLine();
    }

    private void UpdateDueSoon()
    {
        var now = DateTime.Now;
        DateTime weekEnd = WeekStart(now).AddDays(7);
        DueSoonCount = _deadlines.Count(e => e.Start >= now && e.Start < weekEnd);
    }

    // ------------------------------------------------------------- the page

    private void BuildCalendarPage()
    {
        LoadCalendarFromDisk();
        RebuildCalendar();
        _ = RefreshCalendarAsync(force: false);
    }

    private CalendarEntryVm MakeVm(CalendarEntry entry, List<CanvasCourse> termCourses)
    {
        if (entry.CourseId == null && entry.Kind != CalendarKind.Deadline && CalendarCourseMatcher.Match(entry.Title, termCourses) is { } match)
        {
            entry.CourseId = match.Id;
            entry.CourseName = match.Name;
        }
        return new CalendarEntryVm(entry, entry.CourseId != null ? CanvasCourseItem.TintFor(entry.CourseId) : NeutralTint);
    }

    /// <summary>Everything to show between <paramref name="from"/> and <paramref name="to"/>, by what the two switches allow.</summary>
    private List<CalendarEntryVm> CollectEntries(DateTime from, DateTime to)
    {
        var termCourses = CoursesInTerm().ToList();
        var list = new List<CalendarEntryVm>();
        if (ShowDueDates)
            foreach (var e in _deadlines)
                if (e.Start >= from && e.Start < to) list.Add(MakeVm(e, termCourses));
        if (ShowLectures || ShowAllDay)
            foreach (var calendar in _ics.Values)
                foreach (var e in calendar.Expand(from, to))
                {
                    // notices (semester dates, registration phases) and anything all-day follow the all-day switch;
                    // the rest follows the lectures switch
                    if (e.AllDay && !ShowAllDay) continue;
                    if (e.Kind != CalendarKind.Notice && !ShowLectures) continue;
                    list.Add(MakeVm(e, termCourses));
                }
        return list
            .OrderBy(x => x.Start.Date)
            .ThenBy(x => x.Entry.AllDay ? 0 : 1)
            .ThenBy(x => x.Start)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private List<CalendarEntryVm> EntriesOn(DateTime day) =>
        _entries.Where(x => x.Start.Date == day || (x.Entry.AllDay && x.Start.Date <= day && day < x.End.Date)).ToList();

    private CalendarDayVm NewDay(DateTime date, int month) => new(date, date.Month == month);

    /// <summary>Lay out the view that is showing, from the current entries.</summary>
    private void RebuildCalendar()
    {
        if (Page != CanvasPage.Calendar) return;

        DateTime today = DateTime.Today;
        DateTime monthFirst = new(FocusDate.Year, FocusDate.Month, 1);
        var (gridStart, gridWeeks) = MonthGrid(monthFirst);
        var (miniStart, miniWeeks) = MonthGrid(new DateTime(today.Year, today.Month, 1));
        DateTime weekStart = WeekStart(FocusDate);

        DateTime from = new[] { gridStart, miniStart, weekStart }.Min();
        DateTime to = new[] { gridStart.AddDays(gridWeeks * 7), miniStart.AddDays(miniWeeks * 7), weekStart.AddDays(7), today.AddDays(15) }.Max();
        _entries = CollectEntries(from, to);

        switch (CalMode)
        {
            case CalendarViewMode.Month:
                BuildMonth(gridStart, gridWeeks, monthFirst.Month);
                UpdateDayPanel();
                break;
            case CalendarViewMode.Week:
                BuildWeek(weekStart);
                break;
            default:
                BuildAgenda(today);
                BuildMini(miniStart, miniWeeks, today.Month);
                break;
        }
        UpdateCalendarHeader();
        UpdateCalendarStatus();
    }

    private void BuildMonth(DateTime gridStart, int weeks, int month)
    {
        var days = new List<CalendarDayVm>(weeks * 7);
        for (int i = 0; i < weeks * 7; i++)
        {
            DateTime date = gridStart.AddDays(i);
            var day = NewDay(date, month);
            var all = EntriesOn(date);
            foreach (var e in all.Take(3)) day.Chips.Add(e);
            day.MoreCount = Math.Max(0, all.Count - 3);
            day.IsSelected = date == SelectedDate.Date;
            days.Add(day);
        }
        MonthDays.ReplaceAll(days);
    }

    private void BuildWeek(DateTime weekStart)
    {
        _weekStart = weekStart;
        var days = new List<CalendarDayVm>(7);
        var timedByDay = new List<List<CalendarEntryVm>>(7);
        int startHour = 8, endHour = 18;

        for (int i = 0; i < 7; i++)
        {
            DateTime date = weekStart.AddDays(i);
            var day = NewDay(date, date.Month);
            var timed = new List<CalendarEntryVm>();
            foreach (var e in EntriesOn(date))
            {
                if (e.Entry.AllDay || e.IsDeadline) { day.Chips.Add(e); continue; }
                timed.Add(e);
                startHour = Math.Min(startHour, e.Start.Hour);
                endHour = Math.Max(endHour, (int)Math.Ceiling((ClampToDay(e) - date).TotalHours));
            }
            days.Add(day);
            timedByDay.Add(timed);
        }
        endHour = Math.Clamp(endHour, startHour + 1, 24);
        _weekStartHour = startHour;

        for (int i = 0; i < 7; i++)
        {
            var timed = timedByDay[i];
            foreach (var e in timed)
            {
                e.BlockTop = (e.Start.TimeOfDay.TotalHours - startHour) * HourHeight;
                e.BlockHeight = Math.Max(26, (ClampToDay(e) - e.Start).TotalHours * HourHeight - 2);
            }
            AssignLanes(timed);
            foreach (var e in timed) days[i].Timed.Add(e);
        }

        var hours = new List<CalendarHourVm>();
        for (int h = startHour; h < endHour; h++) hours.Add(new CalendarHourVm(h.ToString("00", CultureInfo.InvariantCulture) + ":00"));
        WeekHours.ReplaceAll(hours);
        WeekHeight = (endHour - startHour) * HourHeight;
        WeekDays.ReplaceAll(days);
        UpdateNowLine();
    }

    /// <summary>The end of an entry, cut at midnight (the week grid draws each day on its own).</summary>
    private static DateTime ClampToDay(CalendarEntryVm e) =>
        e.End.Date > e.Start.Date ? e.Start.Date.AddDays(1) : e.End;

    /// <summary>Entries that overlap in time share the width of the column, side by side.</summary>
    private static void AssignLanes(List<CalendarEntryVm> items)
    {
        items.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var cluster = new List<CalendarEntryVm>();
        var laneEnds = new List<DateTime>();
        DateTime clusterEnd = DateTime.MinValue;

        void Close()
        {
            foreach (var e in cluster) e.Cols = Math.Max(1, laneEnds.Count);
            cluster.Clear();
            laneEnds.Clear();
        }

        foreach (var item in items)
        {
            DateTime end = item.End > item.Start ? item.End : item.Start.AddMinutes(30);
            if (cluster.Count > 0 && item.Start >= clusterEnd) Close();
            int lane = laneEnds.FindIndex(laneEnd => laneEnd <= item.Start);
            if (lane < 0)
            {
                laneEnds.Add(end);
                lane = laneEnds.Count - 1;
            }
            else laneEnds[lane] = end;
            item.Col = lane;
            clusterEnd = cluster.Count == 0 ? end : (end > clusterEnd ? end : clusterEnd);
            cluster.Add(item);
        }
        Close();
    }

    private void UpdateNowLine()
    {
        var now = DateTime.Now;
        bool thisWeek = now.Date >= _weekStart && now.Date < _weekStart.AddDays(7);
        double top = (now.TimeOfDay.TotalHours - _weekStartHour) * HourHeight;
        ShowNowLine = thisWeek && top >= 0 && top <= WeekHeight;
        NowLineTop = top;
    }

    private void BuildAgenda(DateTime today)
    {
        var groups = new List<CalendarAgendaGroup>();
        int deadlines = 0, exams = 0, lectures = 0;
        for (int i = 0; i < 14; i++)
        {
            DateTime date = today.AddDays(i);
            var all = EntriesOn(date);
            deadlines += all.Count(e => e.IsDeadline);
            exams += all.Count(e => e.IsExam);
            lectures += all.Count(e => e.IsLecture);
            var shown = all.Where(PassesAgendaFilter).ToList();
            if (shown.Count == 0) continue;
            string heading = i == 0 ? "Today" : i == 1 ? "Tomorrow" : date.ToString("dddd", CultureInfo.CurrentCulture);
            groups.Add(new CalendarAgendaGroup(heading, date.ToString("ddd d MMM", CultureInfo.CurrentCulture), shown));
        }
        AgendaGroups.ReplaceAll(groups);

        var parts = new List<string> { "Next 14 days" };
        if (deadlines > 0) parts.Add(LibraryScanner.Plural(deadlines, "deadline"));
        if (lectures > 0) parts.Add(LibraryScanner.Plural(lectures, "lecture"));
        if (exams > 0) parts.Add(LibraryScanner.Plural(exams, "exam"));
        if (parts.Count == 1) parts.Add("nothing scheduled");
        _agendaSubtitle = string.Join(" · ", parts);
    }

    private bool PassesAgendaFilter(CalendarEntryVm e) => AgendaFilter switch
    {
        "Deadline" => e.IsDeadline,
        "Lecture" => e.IsLecture,
        "Exam" => e.IsExam,
        _ => true,
    };

    private void BuildMini(DateTime gridStart, int weeks, int month)
    {
        var days = new List<CalendarDayVm>(weeks * 7);
        for (int i = 0; i < weeks * 7; i++)
        {
            DateTime date = gridStart.AddDays(i);
            var day = NewDay(date, month);
            foreach (var e in EntriesOn(date))
            {
                if (e.IsDeadline) day.HasDueDot = true;
                else if (e.IsExam) day.HasExamDot = true;
                else if (e.IsLecture) day.HasLectureDot = true;
            }
            day.IsSelected = date == DateTime.Today;
            days.Add(day);
        }
        MiniDays.ReplaceAll(days);
        MiniTitle = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>The month view's side panel: what is on the selected day.</summary>
    private void UpdateDayPanel()
    {
        DateTime day = SelectedDate.Date, today = DateTime.Today;
        DayPanelLabel = day == today ? "TODAY"
            : day == today.AddDays(1) ? "TOMORROW"
            : day == today.AddDays(-1) ? "YESTERDAY"
            : day.ToString("ddd d MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
        DayPanelTitle = day.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        DayEntries.ReplaceAll(EntriesOn(day));
        DayPanelEmpty = DayEntries.Count == 0;
    }

    private void UpdateCalendarHeader()
    {
        switch (CalMode)
        {
            case CalendarViewMode.Month:
            {
                Title = FocusDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
                var parts = new List<string>();
                if (ShowDueDates) parts.Add("Deadlines from Canvas");
                if (ShowLectures) parts.Add(HasCalendarSources ? "lectures from your calendar" : "no calendar linked for lectures yet");
                if (parts.Count == 0) parts.Add("Everything is switched off");
                if (DueSoonCount > 0) parts.Add(DueSoonCount + " due this week");
                Subtitle = string.Join(" · ", parts);
                break;
            }
            case CalendarViewMode.Week:
            {
                DateTime first = WeekStart(FocusDate), last = first.AddDays(6);
                Title = first.Month == last.Month
                    ? first.Day + " – " + last.ToString("d MMMM", CultureInfo.CurrentCulture)
                    : first.ToString("d MMM", CultureInfo.CurrentCulture) + " – " + last.ToString("d MMM", CultureInfo.CurrentCulture);
                var week = _entries.Where(x => x.Start.Date >= first && x.Start.Date <= last).ToList();
                var parts = new List<string> { "Week " + ISOWeek.GetWeekOfYear(first) };
                int deadlines = week.Count(x => x.IsDeadline), lectures = week.Count(x => x.IsLecture), exams = week.Count(x => x.IsExam);
                if (deadlines > 0) parts.Add(LibraryScanner.Plural(deadlines, "deadline"));
                if (lectures > 0) parts.Add(LibraryScanner.Plural(lectures, "lecture"));
                if (exams > 0) parts.Add(LibraryScanner.Plural(exams, "exam"));
                Subtitle = string.Join(" · ", parts);
                break;
            }
            default:
                Title = "Calendar";
                Subtitle = _agendaSubtitle;
                break;
        }
    }

    /// <summary>The "synced 3 min ago" line, the linked calendars, and anything that went wrong.</summary>
    private void UpdateCalendarStatus()
    {
        var sources = CanvasSettings.CalendarSources();
        HasCalendarSources = sources.Count > 0;
        DateTime last = _deadlinesFetchedUtc > _icsFetchedUtc ? _deadlinesFetchedUtc : _icsFetchedUtc;
        string synced = _calendarBusy ? "syncing…"
            : last > DateTime.MinValue.AddDays(1) ? "synced " + LibraryScanner.Ago(last)
            : "not synced yet";

        var rows = new List<CalendarSourceRow>();
        foreach (var source in sources)
        {
            string status = _icsErrors.ContainsKey(source.Id) ? "Couldn't sync"
                : _ics.ContainsKey(source.Id) ? (_calendarBusy ? "Syncing…" : "Synced " + LibraryScanner.Ago(_icsFetchedUtc))
                : (_calendarBusy ? "Syncing…" : "Not synced yet");
            rows.Add(new CalendarSourceRow(source.Name, status));
        }
        CalendarSourceRows.ReplaceAll(rows);

        string names = sources.Count == 0 ? "Canvas" : "Canvas · " + string.Join(", ", sources.Select(s => s.Name));
        CalendarStatus = names + " · " + synced;

        var problems = new List<string>(_icsErrors.Values);
        if (_calendarError != null) problems.Insert(0, _calendarError);
        CalendarMessage = string.Join("  ", problems);
        if (Page == CanvasPage.Calendar) UpdateCalendarHeader();
    }

    // ------------------------------------------------------------- commands

    [RelayCommand] private void ShowCalendar() => GoTo(CanvasPage.Calendar, null);
    [RelayCommand] private void ShowMonth() => SetCalMode(CalendarViewMode.Month);
    [RelayCommand] private void ShowWeek() => SetCalMode(CalendarViewMode.Week);
    [RelayCommand] private void ShowAgenda() => SetCalMode(CalendarViewMode.Agenda);

    private void SetCalMode(CalendarViewMode mode)
    {
        if (CalMode == mode) return;
        if (mode != CalendarViewMode.Agenda) FocusDate = SelectedDate;
        CalMode = mode;
        CanvasSettings.SetCalendarMode(mode.ToString());
    }

    partial void OnCalModeChanged(CalendarViewMode value) => RebuildCalendar();

    partial void OnAgendaFilterChanged(string value)
    {
        if (Page == CanvasPage.Calendar && CalMode == CalendarViewMode.Agenda) RebuildCalendar();
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        foreach (var day in MonthDays) day.IsSelected = day.Date == value.Date;
        if (Page == CanvasPage.Calendar && CalMode == CalendarViewMode.Month) UpdateDayPanel();
    }

    [RelayCommand] private void CalendarPrev() => MoveCalendar(-1);
    [RelayCommand] private void CalendarNext() => MoveCalendar(+1);

    private void MoveCalendar(int direction)
    {
        if (CalMode == CalendarViewMode.Agenda) return;
        FocusDate = CalMode == CalendarViewMode.Month
            ? new DateTime(FocusDate.Year, FocusDate.Month, 1).AddMonths(direction)
            : FocusDate.AddDays(7 * direction);
        // the side panel follows: today if it is in view, else the first day
        DateTime today = DateTime.Today;
        if (CalMode == CalendarViewMode.Month)
        {
            var first = new DateTime(FocusDate.Year, FocusDate.Month, 1);
            SelectedDate = today.Year == first.Year && today.Month == first.Month ? today : first;
        }
        else
        {
            DateTime ws = WeekStart(FocusDate);
            SelectedDate = today >= ws && today < ws.AddDays(7) ? today : ws;
        }
        RebuildCalendar();
    }

    [RelayCommand]
    private void CalendarToday()
    {
        FocusDate = DateTime.Today;
        SelectedDate = DateTime.Today;
        RebuildCalendar();
    }

    [RelayCommand]
    private void SelectDay(CalendarDayVm? day)
    {
        if (day != null) SelectedDate = day.Date;
    }

    [RelayCommand]
    private void SetAgendaFilter(string? filter) => AgendaFilter = filter ?? "All";

    [RelayCommand]
    private void OpenEntry(CalendarEntryVm? entry)
    {
        if (entry is { HasUrl: true }) OpenUrl(entry.Entry.Url);
    }

    [RelayCommand]
    private Task SyncCalendar() => RefreshCalendarAsync(force: true);

    [RelayCommand]
    private async Task AddCalendar()
    {
        if (Owner is not { } owner) return;
        string? url = InputDialog.Show(owner, "Add a calendar",
            "Paste the calendar's .ics address (a webcal:// link works too). Graphite only reads it.");
        if (string.IsNullOrWhiteSpace(url)) return;
        ShowNotice("Checking the calendar…", null, sticky: true);
        var (ok, message) = await CalendarService.AddSourceAsync(url);
        ShowNotice(message, null);
        if (!ok) CalendarMessage = message;
    }
}

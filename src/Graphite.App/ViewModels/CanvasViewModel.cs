using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Interop;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core.Canvas;
using Graphite.Core.Library;
using Microsoft.Win32;

namespace Graphite.App.ViewModels;

/// <summary>
/// The Canvas space of the start screen: the person's courses from Canvas (learning.unisg.ch), each course's
/// start page and modules with all their files, and opening those files straight into Graphite tabs.
///
/// Light on resources by design: nothing talks to Canvas until the space is opened. Opening it loads only the
/// course list; a course's structure loads when the course is clicked, is cached on disk and refreshes quietly
/// in the background when it is opened again. The optional "check every course" pass refreshes at most two
/// courses at a time, current term only, only while the space is open, and skips courses refreshed in the last
/// ten minutes. Nothing is downloaded unless the person clicks.
/// </summary>
public sealed partial class CanvasViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly CanvasCache _cache = new(CanvasSettings.CacheRoot);
    private CanvasClient? _client;
    private string _settingsKey;

    private List<CanvasCourse> _allCourses = new();
    private DateTime _coursesFetchedUtc;
    private bool _loadedFromDisk;
    private bool _active;

    private readonly Dictionary<string, CanvasCourseData?> _data = new();
    private readonly Dictionary<string, HashSet<string>?> _seen = new();
    private readonly Dictionary<string, CanvasFileInfo> _resolved = new();
    private readonly Dictionary<string, HashSet<string>> _collapsed = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly HashSet<string> _busy = new();
    private readonly HashSet<string> _downloading = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _backgroundCts;
    private int _progressDone, _progressTotal;
    private string? _listError;

    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _statusTimer;
    private CanvasPage _pageBeforeSearch = CanvasPage.Overview;

    private static readonly TimeSpan CourseFresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackgroundFresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CourseListFresh = TimeSpan.FromMinutes(30);

    public CanvasViewModel(MainViewModel main)
    {
        _main = main;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplySearch(); };
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); if (!NoticeBusy) Notice = null; };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _statusTimer.Tick += (_, _) => { UpdateStatus(); RefreshCourseStates(); TickCalendar(); };
        CanvasSettings.Changed += OnSettingsChanged;
        CanvasSettings.FolderLinksChanged += OnFolderLinksChanged;
        CanvasSettings.CalendarChanged += OnCalendarSettingsChanged;
        isConnected = CanvasSettings.HasToken;
        _settingsKey = SettingsKey();
    }

    /// <summary>Address and token together: when this changes, the client is built again.</summary>
    private static string SettingsKey() => CanvasSettings.BaseUrl + "|" + (CanvasSettings.ReadToken()?.GetHashCode() ?? 0);

    // ------------------------------------------------------------- state for the views

    /// <summary>Courses of the term on show (the rail and the overview cards).</summary>
    public ObservableCollection<CanvasCourseItem> Courses { get; } = new();

    /// <summary>All terms the course list knows, newest first (the term menu).</summary>
    public ObservableCollection<CanvasTerm> Terms { get; } = new();

    /// <summary>New / Pinned / search results, grouped by course.</summary>
    public ObservableCollection<CanvasSectionVm> Sections { get; } = new();

    /// <summary>The open course: pinned entries, start page links, modules.</summary>
    public ObservableCollection<CanvasRowVm> CoursePins { get; } = new();
    public ObservableCollection<CanvasRowVm> FrontLinks { get; } = new();
    public ObservableCollection<CanvasModuleVm> Modules { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectPage))]
    [NotifyPropertyChangedFor(nameof(IsOverview))]
    [NotifyPropertyChangedFor(nameof(IsNewPage))]
    [NotifyPropertyChangedFor(nameof(IsPinnedPage))]
    [NotifyPropertyChangedFor(nameof(IsCoursePage))]
    [NotifyPropertyChangedFor(nameof(IsSearchPage))]
    [NotifyPropertyChangedFor(nameof(IsCalendarPage))]
    [NotifyPropertyChangedFor(nameof(ShowSections))]
    [NotifyPropertyChangedFor(nameof(LocationLabel))]
    [NotifyPropertyChangedFor(nameof(ShowMarkAllNew))]
    private CanvasPage page = CanvasPage.Overview;

    /// <summary>Raised when the page or the course that is open changed (the "Course tint" material follows it).</summary>
    public event Action? ContextChanged;
    partial void OnPageChanged(CanvasPage value)
    {
        // opening the calendar: the view chosen in Settings, or the one used last
        if (value == CanvasPage.Calendar)
        {
            var open = ParseMode(CanvasSettings.CalendarOpenMode);
            if (open != CalMode) CalMode = open;
        }
        ContextChanged?.Invoke();
    }
    partial void OnCurrentCourseChanged(CanvasCourseItem? value) => ContextChanged?.Invoke();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationLabel))]
    private CanvasCourseItem? currentCourse;

    public bool IsConnectPage => Page == CanvasPage.Connect;
    public bool IsOverview => Page == CanvasPage.Overview;
    public bool IsNewPage => Page == CanvasPage.New;
    public bool IsPinnedPage => Page == CanvasPage.Pinned;
    public bool IsCoursePage => Page == CanvasPage.Course;
    public bool IsSearchPage => Page == CanvasPage.Search;
    public bool ShowSections => Page is CanvasPage.New or CanvasPage.Pinned or CanvasPage.Search;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TermLine))]
    private bool isConnected;

    [ObservableProperty] private string title = "Canvas";
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TermLine))]
    private string termName = "";

    /// <summary>Under "Canvas" at the top of the sidebar: the term on show, or why there is none.</summary>
    public string TermLine => !IsConnected ? "Not connected" : TermName.Length > 0 ? TermName : "No courses loaded yet";
    [ObservableProperty] private string emptyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewTotal))]
    [NotifyPropertyChangedFor(nameof(ShowMarkAllNew))]
    private int newTotal;
    public bool HasNewTotal => NewTotal > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPins))]
    private int pinTotal;
    public bool HasPins => PinTotal > 0;

    // course page
    [ObservableProperty] private bool hasCoursePins;
    [ObservableProperty] private bool hasFront;
    [ObservableProperty] private string frontTitle = "";
    [ObservableProperty] private string frontUrl = "";

    /// <summary>The start page's text, ready to be laid out as a page (null when there is none yet).</summary>
    [ObservableProperty] private IReadOnlyList<CanvasBlock>? frontBlocks;
    [ObservableProperty] private bool hasFrontBody;
    [ObservableProperty] private bool hasFrontLinks;
    [ObservableProperty] private bool hasModules;
    [ObservableProperty] private bool showSkeleton;
    [ObservableProperty] private bool isCourseBusy;
    [ObservableProperty] private string? courseError;
    [ObservableProperty] private string? courseFolder;
    [ObservableProperty] private bool allCollapsed;
    [ObservableProperty] private bool showNoModules;

    public bool IsCourseLinked => CourseFolder != null;
    public string CourseFolderName => CourseFolder == null ? "" : Path.GetFileName(CourseFolder.TrimEnd('\\', '/'));
    partial void OnCourseFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(IsCourseLinked));
        OnPropertyChanged(nameof(CourseFolderName));
    }

    /// <summary>The New page's "Mark all seen" button.</summary>
    public bool ShowMarkAllNew => Page == CanvasPage.New && NewTotal > 0;

    // the bar at the top (token rejected, offline)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    private string? banner;
    [ObservableProperty] private bool bannerOffersSettings;
    public bool HasBanner => Banner != null;

    // the little note at the bottom (download finished, saved to …)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? notice;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoticeFolder))]
    private string? noticePath;
    [ObservableProperty] private bool noticeBusy;
    public bool HasNotice => Notice != null;
    public bool HasNoticeFolder => NoticePath != null && !NoticeBusy;
    partial void OnNoticeBusyChanged(bool value) => OnPropertyChanged(nameof(HasNoticeFolder));

    // search (the command bar's box while the Canvas space is showing)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private string searchText = "";
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public string SearchPlaceholder => "Search all courses";

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>The location pill in the command bar.</summary>
    public string LocationLabel => Page switch
    {
        CanvasPage.Course when CurrentCourse != null => "Canvas › " + (CurrentCourse.Code.Length is > 0 and < 16 ? CurrentCourse.Code : CurrentCourse.Name),
        CanvasPage.New => "Canvas › New",
        CanvasPage.Pinned => "Canvas › Pinned",
        CanvasPage.Calendar => "Canvas › Calendar",
        CanvasPage.Search => "Canvas › Search",
        _ => "Canvas",
    };

    // ------------------------------------------------------------- opening and leaving the space

    /// <summary>The Canvas space was opened: show what is cached at once, then refresh the course list (only the
    /// list) if it is old, and start the optional new-file check.</summary>
    public void Activate()
    {
        _active = true;
        _statusTimer.Start();
        if (!_loadedFromDisk)
        {
            _loadedFromDisk = true;
            if (_cache.LoadCourses() is { } cached)
            {
                _allCourses = cached.Courses;
                _coursesFetchedUtc = cached.FetchedUtc;
            }
            BuildRail();
        }

        LoadCalendarFromDisk();
        IsConnected = CanvasSettings.HasToken;
        if (!IsConnected) { GoTo(CanvasPage.Connect, null); return; }
        if (Page == CanvasPage.Connect) GoTo(CanvasPage.Overview, null);
        else RebuildPage();

        _ = StartAsync();
        CleanCacheOnce();
    }

    private bool _cleaned;

    /// <summary>Once per run, in the background: delete downloaded files that have not been opened for a month, and take
    /// them out of Recent so that list does not point at files that are gone. Files open in a tab are left alone.</summary>
    private void CleanCacheOnce()
    {
        if (_cleaned) return;
        _cleaned = true;
        var open = new HashSet<string>(_main.Documents.Select(d => d.FilePath).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() =>
        {
            try
            {
                var removed = new HashSet<string>(CanvasSettings.PruneCache(open), StringComparer.OrdinalIgnoreCase);
                if (removed.Count == 0) return;
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    ThemeService.RemoveRecentFiles(removed.Contains);
                    foreach (var path in _main.RecentFiles.Where(removed.Contains).ToList()) _main.RecentFiles.Remove(path);
                }, DispatcherPriority.Background);
            }
            catch (Exception ex) { App.LogError("Cleaning the Canvas file cache failed", ex); }
        });
    }

    private async Task StartAsync()
    {
        if (_allCourses.Count == 0 || DateTime.UtcNow - _coursesFetchedUtc > CourseListFresh)
            await RefreshCoursesAsync();
        if (!_active) return;
        if (Page == CanvasPage.Course && CurrentCourse is { } c && IsStale(c.Id, CourseFresh))
            _ = RefreshCourseAsync(c.Id, CancellationToken.None);
        if (CanvasSettings.CheckAllCourses) _ = CheckCoursesAsync(force: false);
        if (IsConnected) _ = RefreshCalendarAsync(force: false);
    }

    /// <summary>The space was left (switched to Local, or the app is going away): stop background work and
    /// remember what was seen in the open course.</summary>
    public void Deactivate()
    {
        _active = false;
        _statusTimer.Stop();
        _backgroundCts?.Cancel();
        if (Page == CanvasPage.Course && CurrentCourse != null) MarkSeen(CurrentCourse.Id);
    }

    // ------------------------------------------------------------- settings

    private void OnSettingsChanged()
    {
        string key = SettingsKey();
        bool connectionChanged = key != _settingsKey;
        _settingsKey = key;
        if (connectionChanged)
        {
            _client?.Dispose();
            _client = null;
            _listError = null;
        }
        IsConnected = CanvasSettings.HasToken;
        Banner = null;
        BuildRail();
        if (!_active) return;

        if (!IsConnected) { GoTo(CanvasPage.Connect, null); return; }
        if (Page == CanvasPage.Connect) GoTo(CanvasPage.Overview, null);
        else RebuildPage();
        if (connectionChanged) _ = StartAsync();
    }

    private void OnFolderLinksChanged(string courseId)
    {
        foreach (var c in Courses) c.Folder = CanvasSettings.FolderOf(c.Id);
        if (Page == CanvasPage.Course && CurrentCourse?.Id == courseId) RebuildPage();
    }

    /// <summary>A client for the saved address and token; null without a usable token.</summary>
    private CanvasClient? Client()
    {
        if (_client != null) return _client;
        string? token = CanvasSettings.ReadToken();
        if (string.IsNullOrEmpty(token))
        {
            if (CanvasSettings.HasToken)
                ShowBanner("The saved Canvas token can't be read on this PC. Paste it again in Settings › Canvas.", settings: true);
            return null;
        }
        try
        {
            _client = new CanvasClient(CanvasSettings.BaseUrl, token);
        }
        catch (ArgumentException ex) { ShowBanner(ex.Message, settings: true); }
        return _client;
    }

    [RelayCommand]
    private void OpenSettings() => _main.OpenSettings("Canvas");

    // ------------------------------------------------------------- course list & terms

    private string CurrentTermId => CanvasParser.PickTerm(_allCourses, DateTime.UtcNow, CanvasSettings.TermId);

    private IEnumerable<CanvasCourse> CoursesInTerm()
    {
        string term = CurrentTermId;
        return _allCourses.Where(c => c.TermId == term).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private CanvasCourse? CourseById(string id) => _allCourses.FirstOrDefault(c => c.Id == id);

    private async Task RefreshCoursesAsync()
    {
        if (Client() is not { } client) return;
        UpdateStatus("Updating course list…");
        try
        {
            var courses = await CanvasFetcher.LoadCoursesAsync(client);
            _allCourses = courses;
            _coursesFetchedUtc = DateTime.UtcNow;
            await Task.Run(() => _cache.SaveCourses(courses));
            _listError = null;
            Banner = null;
        }
        catch (Exception ex) when (ex is CanvasApiException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            _listError = Describe(ex);
            HandleAuth(ex);
        }
        BuildRail();
        if (CurrentCourse != null && Page == CanvasPage.Course && CourseById(CurrentCourse.Id) == null) GoTo(CanvasPage.Overview, null);
        else RebuildPage();
        UpdateStatus();
    }

    /// <summary>The rail: the term's courses with their new counts and folder links.</summary>
    private void BuildRail()
    {
        Terms.Clear();
        foreach (var t in CanvasParser.Terms(_allCourses)) Terms.Add(t);
        string termId = CurrentTermId;
        TermName = Terms.FirstOrDefault(t => t.Id == termId)?.Name ?? "";

        string? activeId = CurrentCourse?.Id;
        Courses.Clear();
        foreach (var c in CoursesInTerm())
        {
            var item = new CanvasCourseItem(c) { Folder = CanvasSettings.FolderOf(c.Id) };
            Courses.Add(item);
        }
        RefreshCounts();
        RefreshCourseStates();
        if (activeId != null && Courses.FirstOrDefault(c => c.Id == activeId) is { } again) CurrentCourse = again;
        UpdateRailSelection();
        CalendarCoursesChanged();
    }

    private void RefreshCounts()
    {
        int total = 0;
        foreach (var c in Courses)
        {
            c.NewCount = CanvasNew.NewKeys(GetData(c.Id), GetSeen(c.Id)).Count;
            total += c.NewCount;
        }
        NewTotal = total;
        PinTotal = CanvasSettings.PinCount;
        if (Page == CanvasPage.Overview) BuildNewPreview();
    }

    private void RefreshCourseStates()
    {
        foreach (var c in Courses)
        {
            c.IsBusy = _busy.Contains(c.Id);
            c.StateText = c.IsBusy ? "Loading…"
                : GetData(c.Id) is { } d ? "Updated " + LibraryScanner.Ago(d.FetchedUtc)
                : "Not loaded yet";
        }
    }

    private void UpdateRailSelection()
    {
        foreach (var c in Courses) c.IsActive = Page == CanvasPage.Course && CurrentCourse != null && c.Id == CurrentCourse.Id;
    }

    [RelayCommand]
    private void SelectTerm(string? termId)
    {
        if (string.IsNullOrEmpty(termId)) return;
        if (Page == CanvasPage.Course && CurrentCourse != null) MarkSeen(CurrentCourse.Id);
        // the term running today is "automatic": it moves on by itself next semester
        string running = CanvasParser.PickTerm(_allCourses, DateTime.UtcNow, null);
        CanvasSettings.SetTerm(termId == running ? null : termId);   // raises Changed → BuildRail
        GoTo(CanvasPage.Overview, null);
        if (CanvasSettings.CheckAllCourses && _active) _ = CheckCoursesAsync(force: false);
    }

    // ------------------------------------------------------------- course data

    private CanvasCourseData? GetData(string id)
    {
        if (!_data.TryGetValue(id, out var d))
        {
            d = _cache.LoadCourse(id);
            _data[id] = d;
        }
        return d;
    }

    private HashSet<string>? GetSeen(string id)
    {
        if (!_seen.TryGetValue(id, out var s))
        {
            s = _cache.LoadSeen(id);
            _seen[id] = s;
        }
        return s;
    }

    private bool IsStale(string id, TimeSpan age) =>
        GetData(id) is not { } d || DateTime.UtcNow - d.FetchedUtc > age
        || d.Front is { Body: null };   // cached before the start page's text was kept: load it once more

    private void MarkSeen(string id)
    {
        if (GetData(id) is not { } d) return;
        var set = GetSeen(id) ?? new HashSet<string>();
        set.UnionWith(CanvasNew.AllKeys(d));
        _seen[id] = set;
        var copy = set.ToList();
        _ = Task.Run(() => _cache.SaveSeen(id, copy));
        if (Courses.FirstOrDefault(c => c.Id == id) is { } item) item.NewCount = 0;
        NewTotal = Courses.Sum(c => c.NewCount);
        if (Page == CanvasPage.Overview) BuildNewPreview();
    }

    // ------------------------------------------------------------- "New since you last looked" on the overview

    /// <summary>At most three courses, each with at most three of its new items (files first).</summary>
    public ObservableCollection<CanvasSectionVm> NewPreview { get; } = new();

    [ObservableProperty] private bool hasNewPreview;
    [ObservableProperty] private string newPreviewSummary = "";

    /// <summary>"+ 2 more courses with new items": shown when the strip could not fit them all.</summary>
    [ObservableProperty] private string newPreviewMore = "";

    private List<CanvasRowVm> NewRowsOf(CanvasCourseItem c)
    {
        var data = GetData(c.Id);
        var newKeys = CanvasNew.NewKeys(data, GetSeen(c.Id));
        if (newKeys.Count == 0) return new List<CanvasRowVm>();
        string? folder = CanvasSettings.FolderOf(c.Id);
        var manifest = folder != null ? CanvasFolderLink.Load(folder) : null;
        var used = new HashSet<string>();
        return CanvasNew.Flatten(data)
            .Where(x => newKeys.Contains(x.Item.Key) && used.Add(x.Item.Key) && Listed(x.Item))
            .OrderBy(x => x.Item.Kind == CanvasItemKind.File ? 0 : 1)
            .Select(x => Row(c.Id, x.Item, manifest, folder, x.Path))
            .ToList();
    }

    private void BuildNewPreview()
    {
        NewPreview.Clear();
        var found = new List<(CanvasCourseItem Course, List<CanvasRowVm> Rows)>();
        foreach (var c in Courses)
        {
            if (c.NewCount == 0) continue;
            var rows = NewRowsOf(c);
            if (rows.Count > 0) found.Add((c, rows));
        }
        foreach (var (course, rows) in found.OrderByDescending(f => f.Rows.Count).Take(3))
        {
            int hidden = rows.Count - 3;
            NewPreview.Add(new CanvasSectionVm(course.Name, course.Id, rows.Take(3),
                hidden > 0 ? $"+ {hidden} more new in this course" : "") { TintBrush = course.TintBrush });
        }
        int total = found.Sum(f => f.Rows.Count);
        int moreCourses = found.Count - NewPreview.Count;
        NewPreviewMore = moreCourses > 0 ? $"+ {moreCourses} more course{(moreCourses == 1 ? "" : "s")} with new items" : "";
        NewPreviewSummary = total == 0 ? "" : $"{LibraryScanner.Plural(total, "item")} in {LibraryScanner.Plural(found.Count, "course")}";
        HasNewPreview = NewPreview.Count > 0;
        BuildCalendarStrip();   // nothing new: the calendar takes the place of the strip
    }

    /// <summary>"Mark seen" on one of the cards of that strip.</summary>
    [RelayCommand]
    private void MarkCourseSeen(string? courseId)
    {
        if (!string.IsNullOrEmpty(courseId)) MarkSeen(courseId);
    }

    private async Task RefreshCourseAsync(string id, CancellationToken ct)
    {
        if (!_busy.Add(id)) return;
        if (Client() is not { } client) { _busy.Remove(id); return; }
        RefreshCourseStates();
        if (CurrentCourse?.Id == id) IsCourseBusy = true;
        bool unchanged = false;
        try
        {
            var fresh = await CanvasFetcher.LoadCourseAsync(client, id, ct);
            unchanged = _data.TryGetValue(id, out var before) && before != null && before.Signature() == fresh.Signature();
            _data[id] = fresh;
            await Task.Run(() => _cache.SaveCourse(id, fresh), CancellationToken.None);
            if (GetSeen(id) == null)
            {
                // first time this course is seen: everything in it is known, nothing is "new"
                var all = CanvasNew.AllKeys(fresh);
                _seen[id] = all;
                var copy = all.ToList();
                await Task.Run(() => _cache.SaveSeen(id, copy), CancellationToken.None);
            }
            _errors.Remove(id);
            if (Banner != null && !BannerOffersSettings) Banner = null;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is CanvasApiException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            _errors[id] = Describe(ex);
            HandleAuth(ex);
        }
        finally
        {
            _busy.Remove(id);
            if (CurrentCourse?.Id == id) IsCourseBusy = false;
            RefreshCounts();
            RefreshCourseStates();
        }

        bool shown = (Page == CanvasPage.Course && CurrentCourse?.Id == id) || Page is CanvasPage.New or CanvasPage.Search;
        if (!shown) return;
        // A refresh that found nothing new leaves the course page alone (rebuilding it would redraw every row, and
        // it would jerk the page if it came in while the person is scrolling); only "updated 2 min ago" changes.
        if (unchanged && Page == CanvasPage.Course && CurrentCourse is { } open && open.Id == id)
            UpdateCourseHeader(open, GetData(id));
        else
            RebuildPage(keepExpansion: true);
    }

    /// <summary>Refresh the term's courses in the background (at most two at a time) so the New list and the
    /// counts are complete. <paramref name="force"/> also refreshes courses that were refreshed recently.</summary>
    private async Task CheckCoursesAsync(bool force)
    {
        if (Client() == null) return;
        _backgroundCts?.Cancel();
        var cts = _backgroundCts = new CancellationTokenSource();
        var ct = cts.Token;

        var ids = CoursesInTerm().Select(c => c.Id)
            .Where(id => force || IsStale(id, BackgroundFresh))
            .ToList();
        if (ids.Count == 0) { UpdateStatus(); return; }

        _progressDone = 0;
        _progressTotal = ids.Count;
        UpdateStatus();
        using var gate = new SemaphoreSlim(2);
        try
        {
            await Task.WhenAll(ids.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    await RefreshCourseAsync(id, ct);
                }
                finally
                {
                    gate.Release();
                    _progressDone++;
                    UpdateStatus();
                }
            }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_backgroundCts, cts))
            {
                _progressDone = _progressTotal = 0;
                UpdateStatus();
            }
        }
    }

    private void UpdateStatus(string? busy = null)
    {
        if (busy != null) StatusText = busy;
        else if (_progressTotal > 1) StatusText = $"Checking courses {Math.Min(_progressDone + 1, _progressTotal)}/{_progressTotal}";
        else if (_busy.Count > 0) StatusText = "Updating…";
        else if (_coursesFetchedUtc > DateTime.MinValue.AddDays(1)) StatusText = "Updated " + LibraryScanner.Ago(_coursesFetchedUtc);
        else StatusText = "";
        if (Page == CanvasPage.Overview) Subtitle = OverviewSubtitle();
    }

    private static string Describe(Exception ex) => ex switch
    {
        CanvasApiException { Status: 401 } => "Canvas rejected the access token. It may be wrong or expired: create a new one and paste it in Settings › Canvas.",
        CanvasApiException { Status: 404 } a => "Not available: " + a.Message,
        CanvasApiException { Status: 403 } a => "Canvas doesn't allow this: " + a.Message,
        CanvasApiException a => "Canvas said: " + a.Message,
        HttpRequestException => "Couldn't reach Canvas. Check your internet connection; cached courses are still shown.",
        IOException io => "Couldn't write the file: " + io.Message,
        _ => ex.Message,
    };

    private void HandleAuth(Exception ex)
    {
        if (ex is CanvasApiException { Status: 401 }) ShowBanner(Describe(ex), settings: true);
        else if (ex is HttpRequestException) ShowBanner(Describe(ex), settings: false);
    }

    private void ShowBanner(string text, bool settings)
    {
        BannerOffersSettings = settings;
        Banner = text;
    }

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    // ------------------------------------------------------------- navigation

    [RelayCommand] private void ShowOverview() => GoTo(CanvasPage.Overview, null);
    [RelayCommand] private void ShowNew() => GoTo(CanvasPage.New, null);
    [RelayCommand] private void ShowPinned() => GoTo(CanvasPage.Pinned, null);

    [RelayCommand]
    private void OpenCourse(CanvasCourseItem? course)
    {
        if (course == null) return;
        GoTo(CanvasPage.Course, course);
        if (IsStale(course.Id, CourseFresh)) _ = RefreshCourseAsync(course.Id, CancellationToken.None);
    }

    /// <summary>Raised before the page changes, so the view can fade the old page out (see CanvasHome).</summary>
    public event Action? PageChanging;

    private void GoTo(CanvasPage page, CanvasCourseItem? course)
    {
        // leaving a course marks what was in it as seen
        if (Page == CanvasPage.Course && CurrentCourse != null && (page != CanvasPage.Course || course?.Id != CurrentCourse.Id))
            MarkSeen(CurrentCourse.Id);

        if (page != CanvasPage.Search && IsSearching)
        {
            _searchTimer.Stop();
            searchText = "";
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsSearching));
        }

        PageChanging?.Invoke();

        void Apply()
        {
            CurrentCourse = course;
            Page = !IsConnected ? CanvasPage.Connect : page;
            UpdateRailSelection();
            RebuildPage();
        }

        // The view may fade the old page out first and then call Apply (like the Local pages do); +1 goes into a
        // course, -1 comes back out of one.
        bool same = Page == page && CurrentCourse?.Id == course?.Id;
        if (Navigate != null && IsConnected && !same)
            Navigate(page == CanvasPage.Course ? 1 : Page == CanvasPage.Course ? -1 : 0, Apply);
        else
            Apply();
    }

    /// <summary>Set by the view: changes the page in two steps (the old one fades out, then the action runs).</summary>
    public Action<int, Action>? Navigate { get; set; }

    private void ApplySearch()
    {
        if (IsSearching)
        {
            if (Page != CanvasPage.Search)
            {
                _pageBeforeSearch = Page;
                if (Page == CanvasPage.Course && CurrentCourse != null) MarkSeen(CurrentCourse.Id);
                Page = CanvasPage.Search;
                UpdateRailSelection();
            }
            RebuildPage();
        }
        else if (Page == CanvasPage.Search)
        {
            var back = _pageBeforeSearch == CanvasPage.Search ? CanvasPage.Overview : _pageBeforeSearch;
            GoTo(back, back == CanvasPage.Course ? CurrentCourse : null);
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    /// <summary>Raised when the page content was rebuilt (the view resets its scroll position on a new page).</summary>
    public event Action? PageBuilt;

    private void RebuildPage(bool keepExpansion = true)
    {
        switch (Page)
        {
            case CanvasPage.Connect:
                Title = "Canvas";
                Subtitle = "Your courses and their files, right in Graphite";
                break;
            case CanvasPage.Overview:
                Title = TermName.Length > 0 ? "Your courses" : "Canvas";
                Subtitle = OverviewSubtitle();
                EmptyText = _listError ?? (StatusText.StartsWith("Updating") ? "Loading your courses…" : "No courses in this term.");
                BuildNewPreview();
                break;
            case CanvasPage.Course when CurrentCourse != null:
                BuildCourse(CurrentCourse);
                break;
            case CanvasPage.New:
                BuildNew();
                break;
            case CanvasPage.Pinned:
                BuildPinned();
                break;
            case CanvasPage.Calendar:
                BuildCalendarPage();
                break;
            case CanvasPage.Search:
                BuildSearch();
                break;
            default:
                Page = CanvasPage.Overview;
                RebuildPage();
                return;
        }
        PageBuilt?.Invoke();
    }

    private string OverviewSubtitle()
    {
        var parts = new List<string>();
        if (TermName.Length > 0) parts.Add(TermName);
        parts.Add(LibraryScanner.Plural(Courses.Count, "course"));
        if (StatusText.Length > 0) parts.Add(StatusText);
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------- building pages

    private CanvasRowVm Row(string courseId, CanvasItem item, CanvasFolderLink.Manifest? manifest, string? folder, string? path = null)
    {
        var row = new CanvasRowVm(courseId, item, path)
        {
            IndentMargin = new Thickness(path == null ? item.Indent * 18 : 0, 0, 0, 0),
            CanSaveToFolder = folder != null,
            IsPinned = CanvasSettings.IsPinned(courseId, item.Key),
        };
        var seen = GetSeen(courseId);
        row.IsNew = seen != null && item.Kind != CanvasItemKind.Header && !seen.Contains(item.Key);
        if (item.Kind == CanvasItemKind.File && item.File != null)
        {
            if (_resolved.TryGetValue(courseId + "|" + item.File.Id, out var info)) row.FileInfo = info;
            if (folder != null && manifest != null) row.OnThisPc = CanvasFolderLink.FindLocal(folder, manifest, item.File.Id) != null;
        }
        return row;
    }

    /// <summary>With "all file types" off, only PDFs and Office files are listed (plus everything that is not a file).</summary>
    private static bool Listed(CanvasItem item)
    {
        if (item.Kind != CanvasItemKind.File || CanvasSettings.AllFileTypes) return true;
        string name = item.File?.Direct?.Name ?? item.Title;
        string ext = CanvasNames.Extension(name);
        // a file whose name says nothing about its type stays (it is most likely a PDF)
        return ext.Length == 0 || ext == "pdf" || LibraryScanner.IsOffice("x." + ext);
    }

    private void BuildCourse(CanvasCourseItem course)
    {
        var data = GetData(course.Id);
        string? folder = CanvasSettings.FolderOf(course.Id);
        var manifest = folder != null ? CanvasFolderLink.Load(folder) : null;
        CourseFolder = folder;
        course.Folder = folder;

        UpdateCourseHeader(course, data);

        int generation = ++_courseBuild;
        _moduleStep = null;
        CoursePins.Clear();
        FrontLinks.Clear();
        Modules.Clear();
        if (data == null)
        {
            HasCoursePins = HasFront = HasFrontBody = HasFrontLinks = HasModules = ShowNoModules = false;
            FrontBlocks = null;
            AllCollapsed = false;
            return;
        }

        var pinnedKeys = new HashSet<string>();
        foreach (var (item, path, _) in CanvasNew.Flatten(data))
            if (CanvasSettings.IsPinned(course.Id, item.Key) && pinnedKeys.Add(item.Key) && Listed(item))
                CoursePins.Add(Row(course.Id, item, manifest, folder, path));
        HasCoursePins = CoursePins.Count > 0;

        HasFront = data.Front != null;
        FrontTitle = data.Front?.Title ?? "";
        FrontUrl = data.Front?.Url ?? "";
        FrontBlocks = FrontBlocksFor(course.Id, data.Front?.Body);
        HasFrontBody = FrontBlocks is { Count: > 0 };
        if (data.Front != null)
            foreach (var l in data.Front.Links.Where(Listed)) FrontLinks.Add(Row(course.Id, l, manifest, folder));
        HasFrontLinks = FrontLinks.Count > 0;

        // The modules come in a few at a time: the first ones at once (they are what is on screen), the rest in
        // the gaps between frames, so opening a big course does not freeze the page for one long layout pass.
        var collapsed = Collapsed(course.Id);
        var waiting = new Queue<CanvasModule>(data.Modules);
        void AddNext() => Modules.Add(NewModule(course.Id, waiting.Dequeue(), manifest, folder, collapsed));

        for (int i = 0; i < FirstModules && waiting.Count > 0; i++) AddNext();
        HasModules = data.Modules.Count > 0;
        ShowNoModules = data.Modules.Count == 0;
        if (waiting.Count == 0)
        {
            AllCollapsed = Modules.Count > 0 && Modules.All(m => !m.IsExpanded);
            return;
        }

        _moduleStep = () =>
        {
            if (waiting.Count == 0) return false;
            AddNext();
            if (waiting.Count > 0) return true;
            AllCollapsed = Modules.All(m => !m.IsExpanded);
            return false;
        };
        Application.Current.Dispatcher.InvokeAsync(() => PumpModules(generation), DispatcherPriority.Background);
    }

    /// <summary>The top of the course page: its name, "ACA · HS26 · updated 5 min ago", errors and the loading skeleton.</summary>
    private void UpdateCourseHeader(CanvasCourseItem course, CanvasCourseData? data)
    {
        Title = course.Name;
        var sub = new List<string>();
        if (course.Code.Length > 0 && course.Code != course.Name) sub.Add(course.Code);
        if (course.Course.TermName.Length > 0) sub.Add(course.Course.TermName);
        if (data != null) sub.Add("updated " + LibraryScanner.Ago(data.FetchedUtc));
        Subtitle = string.Join(" · ", sub);
        CourseError = _errors.TryGetValue(course.Id, out var err) ? err : null;
        IsCourseBusy = _busy.Contains(course.Id);
        ShowSkeleton = data == null && CourseError == null;
    }

    private const int FirstModules = 3;
    private int _courseBuild;
    private Func<bool>? _moduleStep;   // adds the next module that is still waiting; false once none is left

    private void PumpModules(int generation)
    {
        if (generation != _courseBuild || _moduleStep == null) return;
        if (_moduleStep())
            Application.Current.Dispatcher.InvokeAsync(() => PumpModules(generation), DispatcherPriority.Background);
    }

    /// <summary>Add every module that is still waiting (before something that needs them all, like "Collapse all").</summary>
    private void FinishModules()
    {
        var step = _moduleStep;
        if (step == null) return;
        while (step()) { }
    }

    private CanvasModuleVm NewModule(string courseId, CanvasModule m, CanvasFolderLink.Manifest? manifest, string? folder,
        HashSet<string> collapsed)
    {
        var rows = new List<CanvasRowVm>();
        foreach (var it in m.Items.Where(Listed))
        {
            var row = Row(courseId, it, manifest, folder);
            if (it.Links != null)
                foreach (var l in it.Links.Where(Listed)) row.Links.Add(Row(courseId, l, manifest, folder));
            rows.Add(row);
        }
        return new CanvasModuleVm(m, rows, expanded: !collapsed.Contains(m.Id));
    }

    // The start page's text, read into blocks once per course (and again only when the text changes), so a rebuild
    // of the page keeps the same blocks and the view does not lay the page out and fetch its pictures again.
    private readonly Dictionary<string, (string Body, IReadOnlyList<CanvasBlock> Blocks)> _frontBlocks = new();

    private IReadOnlyList<CanvasBlock>? FrontBlocksFor(string courseId, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        if (_frontBlocks.TryGetValue(courseId, out var known) && known.Body == body) return known.Blocks;
        var blocks = CanvasHtmlBlocks.Parse(body, CanvasSettings.BaseUrl);
        _frontBlocks[courseId] = (body, blocks);
        return blocks;
    }

    /// <summary>A link in the start page was clicked: a Canvas file opens in Graphite like its row would, anything
    /// else in the browser.</summary>
    public void OpenPageLink(string href)
    {
        var row = FrontLinks.FirstOrDefault(r => string.Equals(r.Item.Url, href, StringComparison.OrdinalIgnoreCase));
        if (row != null) OpenRowCommand.Execute(row);
        else OpenUrl(href);
    }

    private HashSet<string> Collapsed(string courseId)
    {
        if (!_collapsed.TryGetValue(courseId, out var set)) _collapsed[courseId] = set = new HashSet<string>();
        return set;
    }

    private void BuildNew()
    {
        Title = "New";
        Sections.Clear();
        int total = 0, courses = 0;
        foreach (var c in Courses)
        {
            var data = GetData(c.Id);
            var newKeys = CanvasNew.NewKeys(data, GetSeen(c.Id));
            if (newKeys.Count == 0) continue;
            string? folder = CanvasSettings.FolderOf(c.Id);
            var manifest = folder != null ? CanvasFolderLink.Load(folder) : null;
            var used = new HashSet<string>();
            var rows = CanvasNew.Flatten(data)
                .Where(x => newKeys.Contains(x.Item.Key) && used.Add(x.Item.Key) && Listed(x.Item))
                // files first: they are what the person is after
                .OrderBy(x => x.Item.Kind == CanvasItemKind.File ? 0 : 1)
                .Select(x => Row(c.Id, x.Item, manifest, folder, x.Path))
                .ToList();
            if (rows.Count == 0) continue;
            Sections.Add(new CanvasSectionVm(c.Name, c.Id, rows));
            total += rows.Count;
            courses++;
        }
        bool checking = _progressTotal > 1;
        Subtitle = total == 0
            ? (checking ? "Checking your courses…" : "Nothing new since you last looked")
            : $"{LibraryScanner.Plural(total, "new item")} in {LibraryScanner.Plural(courses, "course")}";
        int unloaded = Courses.Count(c => GetData(c.Id) == null);
        EmptyText = unloaded > 0 && !CanvasSettings.CheckAllCourses
            ? $"{LibraryScanner.Plural(unloaded, "course")} not loaded yet. Open them, or turn on “Check every course for new files” in Settings › Canvas."
            : "Everything is seen. New files show up here when lecturers add them.";
    }

    private void BuildPinned()
    {
        Title = "Pinned";
        Sections.Clear();
        int total = 0;
        foreach (var group in CanvasSettings.Pins.GroupBy(p => p.CourseId))
        {
            var course = CourseById(group.Key);
            var data = GetData(group.Key);
            if (course == null || data == null) continue;
            var keys = group.Select(p => p.Key).ToHashSet();
            string? folder = CanvasSettings.FolderOf(course.Id);
            var manifest = folder != null ? CanvasFolderLink.Load(folder) : null;
            var used = new HashSet<string>();
            var rows = CanvasNew.Flatten(data)
                .Where(x => keys.Contains(x.Item.Key) && used.Add(x.Item.Key))
                .Select(x => Row(course.Id, x.Item, manifest, folder, x.Path)).ToList();
            if (rows.Count == 0) continue;
            Sections.Add(new CanvasSectionVm(course.Name, course.Id, rows));
            total += rows.Count;
        }
        Subtitle = LibraryScanner.Plural(total, "pinned item");
        EmptyText = "Nothing pinned yet. Hover a file and click the pin to keep it here.";
    }

    private void BuildSearch()
    {
        string[] terms = SearchText.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Title = "Search";
        Sections.Clear();
        int total = 0;
        foreach (var course in _allCourses.OrderByDescending(c => c.TermId == CurrentTermId).ThenBy(c => c.Name))
        {
            var data = GetData(course.Id);
            if (data == null) continue;
            string? folder = CanvasSettings.FolderOf(course.Id);
            var manifest = folder != null ? CanvasFolderLink.Load(folder) : null;
            var used = new HashSet<string>();
            var hits = CanvasNew.Flatten(data)
                .Where(x => Listed(x.Item) && used.Add(x.Item.Key) &&
                            terms.All(t => (x.Item.Title + " " + x.Path + " " + course.Name + " " + course.Code).ToLowerInvariant().Contains(t)))
                .ToList();
            if (hits.Count == 0) continue;
            total += hits.Count;
            var rows = hits.Take(60).Select(x => Row(course.Id, x.Item, manifest, folder, x.Path));
            Sections.Add(new CanvasSectionVm(course.Name, course.Id, rows,
                hits.Count > 60 ? $"… and {hits.Count - 60} more. Type a few more letters." : ""));
        }
        Subtitle = $"{LibraryScanner.Plural(total, "match", "matches")} in the courses Graphite has loaded";
        EmptyText = $"Nothing matches “{SearchText.Trim()}”. Only courses that were opened (or checked) are searched.";
    }

    // ------------------------------------------------------------- course page actions

    [RelayCommand]
    private void ToggleModule(CanvasModuleVm? module)
    {
        if (module == null || CurrentCourse == null) return;
        module.IsExpanded = !module.IsExpanded;
        var set = Collapsed(CurrentCourse.Id);
        if (module.IsExpanded) set.Remove(module.Id); else set.Add(module.Id);
        AllCollapsed = Modules.All(m => !m.IsExpanded);
    }

    [RelayCommand]
    private void ToggleAllModules()
    {
        if (CurrentCourse == null) return;
        FinishModules();
        bool expand = AllCollapsed;
        var set = Collapsed(CurrentCourse.Id);
        foreach (var m in Modules)
        {
            m.IsExpanded = expand;
            if (expand) set.Remove(m.Id); else set.Add(m.Id);
        }
        AllCollapsed = !expand;
    }

    [RelayCommand]
    private void MarkAllSeen()
    {
        if (Page == CanvasPage.Course && CurrentCourse != null) MarkSeen(CurrentCourse.Id);
        else foreach (var c in Courses) MarkSeen(c.Id);
        RebuildPage();
    }

    /// <summary>The refresh button: the open course, or (elsewhere) the course list and every course of the term.</summary>
    [RelayCommand]
    private async Task Refresh()
    {
        if (!IsConnected) return;
        if (Page == CanvasPage.Course && CurrentCourse != null)
        {
            await RefreshCourseAsync(CurrentCourse.Id, CancellationToken.None);
            return;
        }
        if (Page == CanvasPage.Calendar)
        {
            await RefreshCalendarAsync(force: true);
            return;
        }
        await RefreshCoursesAsync();
        await CheckCoursesAsync(force: true);
        if (Page == CanvasPage.New) RebuildPage();
    }

    [RelayCommand]
    private void OpenInCanvas()
    {
        string url = Page == CanvasPage.Course && CurrentCourse != null
            ? $"{CanvasSettings.BaseUrl}/courses/{Uri.EscapeDataString(CurrentCourse.Id)}"
            : CanvasSettings.BaseUrl;
        OpenUrl(url);
    }

    [RelayCommand]
    private void OpenFrontPage()
    {
        if (FrontUrl.Length > 0) OpenUrl(FrontUrl);
    }

    [RelayCommand]
    private void LinkFolder()
    {
        if (CurrentCourse is not { } course) return;
        var dialog = new OpenFolderDialog
        {
            Title = $"Link “{course.Name}” to a folder",
            InitialDirectory = CourseFolder ?? _main.Library.RootPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(Owner) != true) return;
        string folder = dialog.FolderName;

        var manifest = CanvasFolderLink.Load(folder);
        if (manifest.CourseId.Length > 0 && manifest.CourseId != course.Id)
        {
            string other = CourseById(manifest.CourseId)?.Name ?? "another course";
            if (MessageDialog.Show(Owner, $"This folder is already linked to {other}. Link it to {course.Name} instead?",
                    "Link folder", DialogButtons.YesNo, DialogIcon.Info) != MessageBoxResult.Yes)
                return;
            manifest = new CanvasFolderLink.Manifest();
        }
        manifest.CourseId = course.Id;
        CanvasFolderLink.Save(folder, manifest);
        CanvasSettings.SetFolder(course.Id, folder);   // raises FolderLinksChanged → the page is rebuilt
        ShowNotice($"{course.Name} is linked to {Path.GetFileName(folder.TrimEnd('\\', '/'))}. Files you open are saved there.", folder);
    }

    [RelayCommand]
    private void UnlinkFolder()
    {
        if (CurrentCourse is not { } course || CourseFolder == null) return;
        CanvasSettings.SetFolder(course.Id, null);
        ShowNotice($"{course.Name} is no longer linked. Files already in the folder stay there.", null);
    }

    [RelayCommand]
    private void OpenCourseFolder()
    {
        if (CourseFolder is { } f) Reveal(f, select: false);
    }

    // ------------------------------------------------------------- rows

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenRow(CanvasRowVm? row)
    {
        if (row == null || row.IsHeader) return;
        if (row.IsFile) await OpenFileAsync(row);
        else if (row.Item.Url.Length > 0) OpenUrl(row.Item.Url);
    }

    [RelayCommand]
    private void OpenRowInBrowser(CanvasRowVm? row)
    {
        if (row == null) return;
        string url = row.Item.File?.Href is { Length: > 0 } href ? href : row.Item.Url;
        if (url.Length > 0) OpenUrl(url);
    }

    [RelayCommand]
    private void TogglePin(CanvasRowVm? row)
    {
        if (row == null || row.IsHeader) return;
        row.IsPinned = CanvasSettings.TogglePin(row.CourseId, row.Key);
        PinTotal = CanvasSettings.PinCount;
        // the same entry may show twice on a page (pinned section and its module)
        foreach (var other in VisibleRows().Where(r => r.CourseId == row.CourseId && r.Key == row.Key)) other.IsPinned = row.IsPinned;
        if (Page == CanvasPage.Course || Page == CanvasPage.Pinned) RebuildPage();
    }

    private IEnumerable<CanvasRowVm> VisibleRows()
    {
        foreach (var r in CoursePins) yield return r;
        foreach (var r in FrontLinks) yield return r;
        foreach (var m in Modules)
            foreach (var r in m.Rows)
            {
                yield return r;
                foreach (var l in r.Links) yield return l;
            }
        foreach (var s in Sections)
            foreach (var r in s.Rows) yield return r;
    }

    /// <summary>The download facts of a file row (asked from Canvas when the module did not carry them).</summary>
    private async Task<CanvasFileInfo?> ResolveAsync(CanvasRowVm row)
    {
        if (row.FileInfo is { Url.Length: > 0 } known) return known;
        if (Client() is not { } client) return null;
        try
        {
            var info = await CanvasFetcher.ResolveFileAsync(client, row.CourseId, row.Item);
            if (row.Item.File != null) _resolved[row.CourseId + "|" + row.Item.File.Id] = info;
            row.FileInfo = info;
            return info;
        }
        catch (Exception ex) when (ex is CanvasApiException or HttpRequestException)
        {
            HandleAuth(ex);
            // Canvas won't hand out the file (locked, or not published to the API): its page may still work
            if (MessageDialog.Show(Owner, $"Graphite couldn't get “{row.Title}” from Canvas.\n{Describe(ex)}\n\nOpen it in your browser instead?",
                    "Canvas", DialogButtons.YesNo, DialogIcon.Info) == MessageBoxResult.Yes)
                OpenRowInBrowser(row);
            return null;
        }
    }

    /// <summary>Download a file to <paramref name="target"/> (through a temporary name, so a broken download never
    /// leaves half a file behind).</summary>
    private async Task<bool> DownloadAsync(CanvasFileInfo info, string target, string label)
    {
        if (Client() is not { } client) return false;
        if (!_downloading.Add(target)) return false;   // already on its way
        string part = target + ".graphite-part";
        NoticeBusy = true;
        ShowNotice($"Downloading “{label}”…", null, sticky: true);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            long? size = info.Size;
            var progress = new Progress<long>(done =>
            {
                if (size is long total && total > 0) Notice = $"Downloading “{label}”… {Math.Min(100, done * 100 / total)} %";
            });
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await client.DownloadAsync(info.Url, fs, progress);
            File.Move(part, target, overwrite: true);
            if (info.UpdatedUtc is { } u) TrySetWriteTime(target, u);
            return true;
        }
        catch (Exception ex) when (ex is CanvasApiException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            NoticeBusy = false;
            Notice = null;
            HandleAuth(ex);
            MessageDialog.Show(Owner, $"Couldn't download “{label}”.\n{Describe(ex)}", "Canvas", DialogButtons.OK, DialogIcon.Warning);
            return false;
        }
        finally
        {
            _downloading.Remove(target);
            if (NoticeBusy) { NoticeBusy = false; Notice = null; }
        }
    }

    private static void TrySetWriteTime(string path, DateTime utc)
    {
        try { File.SetLastWriteTimeUtc(path, utc); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The file cache copy of a Canvas file: one folder per file id, so two files with the same name
    /// never collide.</summary>
    private static string CachePath(string courseId, CanvasItem item, CanvasFileInfo info) =>
        Path.Combine(CanvasSettings.FileCacheRoot, CanvasNames.SafeId(courseId),
            CanvasNames.SafeId(item.File?.Id is { Length: > 0 } id ? id : item.Key), CanvasNames.SafeFileName(info.Name.Length > 0 ? info.Name : item.Title));

    /// <summary>A cached copy is good while it has the size Canvas reports and is not older than the file in Canvas.</summary>
    private static bool UpToDate(string path, CanvasFileInfo info)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return false;
        if (info.Size is { } s && fi.Length != s) return false;
        if (info.UpdatedUtc is { } u && fi.LastWriteTimeUtc < u.AddSeconds(-2)) return false;
        return true;
    }

    /// <summary>
    /// Where the file is on this PC, downloading it first if needed. A linked course keeps its files in its folder
    /// (matched by Canvas id, so renamed files are still found); any other course uses the cache.
    /// <paramref name="intoFolder"/> forces the course folder (Save to course folder).
    /// </summary>
    private async Task<(string? Path, bool FromCache)> EnsureLocalAsync(CanvasRowVm row, CanvasFileInfo info, bool intoFolder = false)
    {
        string? folder = CanvasSettings.FolderOf(row.CourseId);
        string fileId = row.Item.File?.Id ?? "";
        if (folder != null)
        {
            var manifest = CanvasFolderLink.Load(folder);
            var (path, exists) = CanvasFolderLink.Place(folder, manifest, fileId, info.Name.Length > 0 ? info.Name : row.Title, info.Size);
            if (!exists)
            {
                // a copy already in the cache saves a second download
                string cached = CachePath(row.CourseId, row.Item, info);
                if (UpToDate(cached, info))
                {
                    try { File.Copy(cached, path, overwrite: false); exists = true; }
                    catch (IOException) { }
                }
                if (!exists && !await DownloadAsync(info, path, row.Title)) return (null, false);
            }
            CanvasFolderLink.Record(folder, row.CourseId, fileId, path);
            foreach (var r in VisibleRows().Where(r => r.CourseId == row.CourseId && r.Key == row.Key)) r.OnThisPc = true;
            return (path, false);
        }
        if (intoFolder) return (null, false);

        string target = CachePath(row.CourseId, row.Item, info);
        if (!UpToDate(target, info) && !await DownloadAsync(info, target, row.Title)) return (null, true);
        CanvasSettings.TouchCacheFile(target);   // the clean-up keeps what was used lately
        return (target, true);
    }

    private async Task OpenFileAsync(CanvasRowVm row)
    {
        var info = await ResolveAsync(row);
        if (info == null) return;
        var (path, _) = await EnsureLocalAsync(row, info);
        if (path == null) return;
        MarkRowSeen(row);

        if (LibraryScanner.IsSupported(path, includeOffice: true))
            await _main.OpenFilesAsync(new[] { path }, resume: true);
        else
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageDialog.Show(Owner, $"Windows couldn't open this file.\n{ex.Message}", "Graphite", DialogButtons.OK, DialogIcon.Info);
            }
        }
    }

    /// <summary>Opening a file counts as having seen it.</summary>
    private void MarkRowSeen(CanvasRowVm row)
    {
        if (!row.IsNew) return;
        var set = GetSeen(row.CourseId) ?? new HashSet<string>();
        set.Add(row.Key);
        _seen[row.CourseId] = set;
        var copy = set.ToList();
        _ = Task.Run(() => _cache.SaveSeen(row.CourseId, copy));
        foreach (var r in VisibleRows().Where(r => r.CourseId == row.CourseId && r.Key == row.Key)) r.IsNew = false;
        RefreshCounts();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task QuickDownload(CanvasRowVm? row)
    {
        if (row is not { IsFile: true } || await ResolveAsync(row) is not { } info) return;
        string target = CanvasNames.UniquePath(KnownFolders.Downloads, CanvasNames.SafeFileName(info.Name.Length > 0 ? info.Name : row.Title));
        if (await CopyOrDownloadAsync(row, info, target))
        {
            MarkRowSeen(row);
            ShowNotice($"Saved “{Path.GetFileName(target)}” to Downloads.", target);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveFileAs(CanvasRowVm? row)
    {
        if (row is not { IsFile: true } || await ResolveAsync(row) is not { } info) return;
        string name = CanvasNames.SafeFileName(info.Name.Length > 0 ? info.Name : row.Title);
        string ext = Path.GetExtension(name);
        var dialog = new SaveFileDialog
        {
            FileName = name,
            InitialDirectory = CanvasSettings.FolderOf(row.CourseId) ?? KnownFolders.Downloads,
            Filter = ext.Length > 1 ? $"{ext.TrimStart('.').ToUpperInvariant()} file|*{ext}|All files|*.*" : "All files|*.*",
        };
        if (dialog.ShowDialog(Owner) != true) return;
        if (await CopyOrDownloadAsync(row, info, dialog.FileName))
        {
            MarkRowSeen(row);
            ShowNotice($"Saved “{Path.GetFileName(dialog.FileName)}”.", dialog.FileName);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SaveToCourseFolder(CanvasRowVm? row)
    {
        if (row is not { IsFile: true } || CanvasSettings.FolderOf(row.CourseId) is not { } folder) return;
        if (await ResolveAsync(row) is not { } info) return;
        var (path, _) = await EnsureLocalAsync(row, info, intoFolder: true);
        if (path == null) return;
        MarkRowSeen(row);
        ShowNotice($"“{Path.GetFileName(path)}” is in {Path.GetFileName(folder.TrimEnd('\\', '/'))}.", path);
    }

    /// <summary>A copy that is already on this PC is copied instead of downloaded again.</summary>
    private async Task<bool> CopyOrDownloadAsync(CanvasRowVm row, CanvasFileInfo info, string target)
    {
        string? local = null;
        if (CanvasSettings.FolderOf(row.CourseId) is { } folder &&
            CanvasFolderLink.FindLocal(folder, CanvasFolderLink.Load(folder), row.Item.File?.Id ?? "") is { } inFolder)
            local = inFolder;
        else if (CachePath(row.CourseId, row.Item, info) is var cached && UpToDate(cached, info))
            local = cached;

        if (local != null && !LibraryScanner.SamePath(local, target))
        {
            try
            {
                await Task.Run(() => File.Copy(local, target, overwrite: true));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageDialog.Show(Owner, $"Couldn't save the file.\n{ex.Message}", "Canvas", DialogButtons.OK, DialogIcon.Warning);
                return false;
            }
        }
        return await DownloadAsync(info, target, row.Title);
    }

    /// <summary>Every file of a module (including those linked inside its pages) in one ZIP.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadModuleZip(CanvasModuleVm? module)
    {
        if (module == null || CurrentCourse is not { } course) return;
        var files = CanvasNew.ModuleFiles(module.Model).Where(Listed).ToList();
        if (files.Count == 0) return;

        string baseName = CanvasNames.SafeFileName($"{(course.Code.Length is > 0 and < 16 ? course.Code : course.Name)} - {module.Name}");
        var dialog = new SaveFileDialog
        {
            FileName = baseName + ".zip",
            InitialDirectory = KnownFolders.Downloads,
            Filter = "Zip archive|*.zip",
        };
        if (dialog.ShowDialog(Owner) != true) return;

        var gathered = new List<(string Path, string Name)>();
        int failed = 0;
        NoticeBusy = true;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                ShowNotice($"Preparing ZIP… {i + 1}/{files.Count}", null, sticky: true);
                var row = Row(course.Id, files[i], null, null);
                try
                {
                    if (Client() is not { } client) { failed++; continue; }
                    var info = row.FileInfo is { Url.Length: > 0 } k ? k : await CanvasFetcher.ResolveFileAsync(client, course.Id, files[i]);
                    if (files[i].File != null) _resolved[course.Id + "|" + files[i].File!.Id] = info;
                    string? local = null;
                    if (CanvasSettings.FolderOf(course.Id) is { } folder &&
                        CanvasFolderLink.FindLocal(folder, CanvasFolderLink.Load(folder), files[i].File?.Id ?? "") is { } inFolder)
                        local = inFolder;
                    else
                    {
                        string cached = CachePath(course.Id, files[i], info);
                        if (UpToDate(cached, info) || await DownloadQuietAsync(client, info, cached)) local = cached;
                    }
                    if (local == null) { failed++; continue; }
                    gathered.Add((local, CanvasNames.SafeFileName(info.Name.Length > 0 ? info.Name : files[i].Title)));
                }
                catch (Exception ex) when (ex is CanvasApiException or HttpRequestException or IOException)
                {
                    failed++;
                }
            }

            if (gathered.Count == 0)
            {
                MessageDialog.Show(Owner, "None of the files could be downloaded. Try them one by one.", "Canvas", DialogButtons.OK, DialogIcon.Warning);
                return;
            }

            string zipPath = dialog.FileName;
            await Task.Run(() =>
            {
                string tmp = zipPath + ".graphite-part";
                if (File.Exists(tmp)) File.Delete(tmp);
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (path, name) in gathered)
                    {
                        string entry = name;
                        for (int n = 2; !used.Add(entry); n++)
                            entry = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
                        // PDFs and slides are already compressed: storing them is as small and much faster
                        zip.CreateEntryFromFile(path, entry, CompressionLevel.Fastest);
                    }
                }
                File.Move(tmp, zipPath, overwrite: true);
            });
            NoticeBusy = false;
            ShowNotice(failed == 0
                ? $"Saved “{Path.GetFileName(zipPath)}” ({LibraryScanner.Plural(gathered.Count, "file")})."
                : $"Saved “{Path.GetFileName(zipPath)}”. {LibraryScanner.Plural(failed, "file")} couldn't be included.", zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageDialog.Show(Owner, $"Couldn't write the ZIP.\n{ex.Message}", "Canvas", DialogButtons.OK, DialogIcon.Warning);
        }
        finally
        {
            if (NoticeBusy) { NoticeBusy = false; Notice = null; }
        }
    }

    private static async Task<bool> DownloadQuietAsync(CanvasClient client, CanvasFileInfo info, string target)
    {
        string part = target + ".graphite-part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await client.DownloadAsync(info.Url, fs);
            File.Move(part, target, overwrite: true);
            if (info.UpdatedUtc is { } u) TrySetWriteTime(target, u);
            return true;
        }
        catch (Exception ex) when (ex is CanvasApiException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            return false;
        }
    }

    // ------------------------------------------------------------- notice

    private void ShowNotice(string text, string? path, bool sticky = false)
    {
        Notice = text;
        NoticePath = path;
        _noticeTimer.Stop();
        if (!sticky) _noticeTimer.Start();
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer.Stop();
        Notice = null;
    }

    [RelayCommand]
    private void RevealNotice()
    {
        if (NoticePath is { } p) Reveal(p, select: File.Exists(p));
    }

    // ------------------------------------------------------------- helpers

    private static Window? Owner => Application.Current?.MainWindow;

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.LogError("Opening a Canvas link failed", ex);
        }
    }

    private static void Reveal(string path, bool select)
    {
        try
        {
            if (select) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.LogError("Show in Explorer failed", ex);
        }
    }
}

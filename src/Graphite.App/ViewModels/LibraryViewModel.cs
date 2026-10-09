using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core.Library;

namespace Graphite.App.ViewModels;

/// <summary>
/// The start screen: a window onto the folder the person picked once (usually a OneDrive
/// folder). Home shows its sub-folders as cards, "continue reading" and recent files; a folder
/// shows its PDFs and nested folders; Recent and Pinned list files from anywhere. The same
/// search box scopes to wherever you are. Everything is read-only: nothing is moved, copied or
/// uploaded, and online-only OneDrive files are listed without being downloaded.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly List<string> _roots = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _watchTimer;
    private readonly DispatcherTimer _clockTimer;

    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private List<LibraryFolder> _rootFolders = new();
    private int _rootFileCount;
    private bool _railLoaded;
    private bool _suppressSearch;
    private DateTime _lastRefreshUtc = DateTime.UtcNow;

    public LibraryViewModel(MainViewModel main)
    {
        _main = main;
        sortMode = ThemeService.LibrarySort is "Modified" or "Size" ? ThemeService.LibrarySort : "Name";
        isGrid = ThemeService.LibraryGrid;
        includeOffice = ThemeService.LibraryIncludeOffice;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = ReloadAsync(rescanRail: false); };

        // File-system events arrive in bursts (a sync run touches many files): wait for quiet.
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _watchTimer.Tick += (_, _) => { _watchTimer.Stop(); _ = ReloadAsync(rescanRail: true); };

        // One tick a second keeps the big clock on the start screen exact; the sync label only
        // needs refreshing now and then.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => OnClockTick();
        _clockTimer.Start();
        UpdateClock();
        Files.CollectionChanged += (_, _) => RecountSelection();
    }

    // ------------------------------------------------------------- state

    public ObservableCollection<RailItem> RailItems { get; } = new();
    public ObservableCollection<FolderCardViewModel> Folders { get; } = new();
    public ObservableCollection<FileCardViewModel> Files { get; } = new();
    public ObservableCollection<FileCardViewModel> Recents { get; } = new();
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeActive))]
    [NotifyPropertyChangedFor(nameof(IsRecentActive))]
    [NotifyPropertyChangedFor(nameof(IsPinnedActive))]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowFilesBody))]
    [NotifyPropertyChangedFor(nameof(ShowBreadcrumb))]
    [NotifyPropertyChangedFor(nameof(ShowChips))]
    [NotifyPropertyChangedFor(nameof(ShowTypeToggle))]
    [NotifyPropertyChangedFor(nameof(CanSort))]
    [NotifyPropertyChangedFor(nameof(SearchPlaceholder))]
    private LibraryView currentView = LibraryView.Home;

    [ObservableProperty] private string? currentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoot))]
    [NotifyPropertyChangedFor(nameof(RootName))]
    [NotifyPropertyChangedFor(nameof(CanSort))]
    [NotifyPropertyChangedFor(nameof(ShowFoldersSection))]
    [NotifyPropertyChangedFor(nameof(SearchPlaceholder))]
    private string? rootPath;

    [ObservableProperty] private string title = "Home";
    [ObservableProperty] private string subtitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    [NotifyPropertyChangedFor(nameof(ShowHomeSections))]
    [NotifyPropertyChangedFor(nameof(ShowFilesBody))]
    [NotifyPropertyChangedFor(nameof(ShowChips))]
    [NotifyPropertyChangedFor(nameof(CanSort))]
    private string searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    private string sortMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList))]
    private bool isGrid;

    /// <summary>Inside a folder: list every kind of file, not only PDFs (and Office files, if chosen).
    /// Non-PDF files are opened by Windows with whatever app handles them.</summary>
    [ObservableProperty] private bool showAllTypes;

    /// <summary>The rail's main-folder row can fold its subfolders away.</summary>
    [ObservableProperty] private bool isRailExpanded = true;

    [ObservableProperty] private bool includeOffice;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string emptyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContinue))]
    [NotifyPropertyChangedFor(nameof(ContinueColumnWidth))]
    [NotifyPropertyChangedFor(nameof(DropColumnWidth))]
    private FileCardViewModel? continueItem;

    [ObservableProperty] private bool hasRecents;
    [ObservableProperty] private bool hasFiles;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFoldersSection))]
    [NotifyPropertyChangedFor(nameof(ShowChips))]
    private bool hasFolders;

    public bool HasRoot => RootPath != null;
    public string RootName => RootPath == null ? "" : FolderName(RootPath);
    public bool IsHomeActive => CurrentView == LibraryView.Home;
    public bool IsRecentActive => CurrentView == LibraryView.Recent;
    public bool IsPinnedActive => CurrentView == LibraryView.Pinned;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public bool IsList => !IsGrid;

    public bool ShowHomeSections => CurrentView == LibraryView.Home && !IsSearching;
    public bool ShowFilesBody => !ShowHomeSections;
    public bool ShowBreadcrumb => CurrentView == LibraryView.Folder;
    public bool ShowContinue => ContinueItem != null;

    // "Continue reading" and the open-elsewhere box share a row: the box takes the whole row
    // when there is nothing to continue.
    public GridLength ContinueColumnWidth => ShowContinue ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    public GridLength DropColumnWidth => ShowContinue ? new GridLength(340) : new GridLength(1, GridUnitType.Star);

    public string VersionText => $"Version {_main.AppVersion}";

    [RelayCommand]
    private void CheckUpdates() => _main.CheckForUpdatesCommand.Execute(null);
    public bool ShowFoldersSection => HasRoot && HasFolders;
    public bool ShowChips => HasFolders && CurrentView == LibraryView.Folder && !IsSearching;
    public bool ShowTypeToggle => CurrentView == LibraryView.Folder;
    public bool CanSort => !IsSearching && (CurrentView == LibraryView.Folder || (CurrentView == LibraryView.Home && HasRoot));
    public string SortLabel => $"Sort: {SortMode}";

    public string SearchPlaceholder => CurrentView switch
    {
        LibraryView.Home when HasRoot => $"Search all of {RootName}",
        LibraryView.Folder => $"Search in {Title}",
        LibraryView.Recent => "Search recent files",
        LibraryView.Pinned => "Search pinned files",
        _ => "Search files",
    };

    // ------------------------------------------------------------- start-up & roots

    /// <summary>Load the saved library (call once, after the window exists).</summary>
    public void Initialize()
    {
        foreach (var r in ThemeService.LibraryRoots)
            if (Directory.Exists(r)) _roots.Add(r);

        string? active = ThemeService.ActiveLibraryRoot;
        if (active == null || !_roots.Any(r => LibraryScanner.SamePath(r, active))) active = _roots.FirstOrDefault();
        ActivateRoot(active, save: false);
    }

    private void ActivateRoot(string? path, bool save)
    {
        RootPath = path;
        _rootFolders = new();
        _rootFileCount = 0;
        _railLoaded = false;
        Folders.Clear();
        Files.Clear();
        Recents.Clear();
        ContinueItem = null;
        HasFolders = false;
        HasFiles = false;
        HasRecents = false;
        CurrentPath = null;
        CurrentView = LibraryView.Home;
        _history.Clear();
        _historyIndex = -1;
        RecordNav();
        ClearSearchSilently();
        StartWatcher(path);
        if (save) ThemeService.SetLibrary(_roots, path, IncludeOffice);
        BuildRail();
        _ = ReloadAsync(rescanRail: true);
    }

    private void StartWatcher(string? root)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (root == null) return;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, _) => QueueRefresh();
            w.Deleted += (_, _) => QueueRefresh();
            w.Changed += (_, _) => QueueRefresh();
            w.Renamed += (_, _) => QueueRefresh();
            w.Error += (_, _) => QueueRefresh();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            // Not watchable (network share, permissions): the Refresh menu item still works.
            Debug.WriteLine($"Library watcher not started: {ex.Message}");
        }
    }

    private void QueueRefresh() =>
        Application.Current?.Dispatcher.BeginInvoke(() => { _watchTimer.Stop(); _watchTimer.Start(); });

    // ------------------------------------------------------------- navigation

    [RelayCommand]
    private void ShowHome()
    {
        ClearSearchSilently();
        CurrentView = LibraryView.Home;
        CurrentPath = null;
        RecordNav();
        UpdateRailSelection();
        _ = ReloadAsync(rescanRail: !_railLoaded);
    }

    [RelayCommand]
    private void ShowRecent()
    {
        ClearSearchSilently();
        CurrentView = LibraryView.Recent;
        CurrentPath = null;
        RecordNav();
        UpdateRailSelection();
        _ = ReloadAsync(rescanRail: false);
    }

    [RelayCommand]
    private void ShowPinned()
    {
        ClearSearchSilently();
        CurrentView = LibraryView.Pinned;
        CurrentPath = null;
        RecordNav();
        UpdateRailSelection();
        _ = ReloadAsync(rescanRail: false);
    }

    [RelayCommand]
    private void ShowFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        ClearSearchSilently();
        CurrentView = LibraryView.Folder;
        CurrentPath = path;
        RecordNav();
        UpdateRailSelection();
        _ = ReloadAsync(rescanRail: !_railLoaded);
    }

    /// <summary>A breadcrumb was clicked: the library root is the home page, anything else a folder.</summary>
    [RelayCommand]
    private void OpenCrumb(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (RootPath != null && LibraryScanner.SamePath(path, RootPath)) ShowHome();
        else ShowFolder(path);
    }

    [RelayCommand]
    private void OpenRailItem(RailItem? item)
    {
        if (item == null) return;
        switch (item.Kind)
        {
            case RailKind.Root:
                if (RootPath != null && LibraryScanner.SamePath(item.Path, RootPath)) ShowHome();
                else
                {
                    ActivateRoot(item.Path, save: true);
                }
                break;
            default:
                ShowFolder(item.Path);
                break;
        }
    }

    /// <summary>One level up (the breadcrumb's back arrow).</summary>
    [RelayCommand]
    private void GoUp()
    {
        if (CurrentPath == null || RootPath == null) { ShowHome(); return; }
        string? parent = Path.GetDirectoryName(CurrentPath.TrimEnd('\\', '/'));
        if (parent == null || !LibraryScanner.IsSameOrChild(RootPath, parent) ||
            LibraryScanner.SamePath(parent, RootPath))
            ShowHome();
        else
            ShowFolder(parent);
    }

    /// <summary>The start screen is shown again after reading: refresh "continue reading",
    /// recents and the progress bars without rescanning any folder.</summary>
    public void OnHomeShown()
    {
        foreach (var card in Files) card.RefreshReading();
        foreach (var card in Recents) card.RefreshReading();
        if (CurrentView == LibraryView.Home && !IsSearching) BuildRecentsAndContinue();
        else if (CurrentView == LibraryView.Recent) _ = ReloadAsync(rescanRail: false);
        UpdateStatus();
    }

    // ------------------------------------------------------------- choosing folders

    [RelayCommand] private void ChooseFolder() => PickFolder(add: false);
    [RelayCommand] private void AddFolder() => PickFolder(add: true);

    private void PickFolder(bool add)
    {
        var choice = ChooseFolderDialog.Show(Application.Current?.MainWindow, RootPath, IncludeOffice);
        if (choice is not { } picked) return;

        IncludeOffice = picked.IncludeOffice;
        if (!add && RootPath != null)
            _roots.RemoveAll(r => LibraryScanner.SamePath(r, RootPath));
        if (!_roots.Any(r => LibraryScanner.SamePath(r, picked.Path))) _roots.Add(picked.Path);
        ActivateRoot(picked.Path, save: true);
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (RootPath == null) return;
        _roots.RemoveAll(r => LibraryScanner.SamePath(r, RootPath));
        ActivateRoot(_roots.FirstOrDefault(), save: true);
    }

    [RelayCommand]
    private void Refresh() => _ = ReloadAsync(rescanRail: true);

    partial void OnIncludeOfficeChanged(bool value) => ThemeService.SetLibrary(_roots, RootPath, value);

    // ------------------------------------------------------------- view options

    [RelayCommand]
    private void SetSort(string? mode)
    {
        if (mode is "Name" or "Modified" or "Size") SortMode = mode;
    }

    /// <summary>"grid" or "list" (a string so it can be a XAML CommandParameter).</summary>
    [RelayCommand]
    private void SetViewMode(string? mode) => IsGrid = mode != "list";

    partial void OnSortModeChanged(string value)
    {
        ThemeService.SetLibraryView(value, IsGrid);
        if (CurrentView is LibraryView.Home or LibraryView.Folder) _ = ReloadAsync(rescanRail: false);
    }

    partial void OnIsGridChanged(bool value) => ThemeService.SetLibraryView(SortMode, value);

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearchSilently()
    {
        _searchTimer.Stop();
        _suppressSearch = true;
        SearchText = "";
        _suppressSearch = false;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchTimer.Stop();
        SearchText = "";
        _ = ReloadAsync(rescanRail: false);
    }

    // ------------------------------------------------------------- files

    [RelayCommand]
    private async Task OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path))
        {
            MessageDialog.Show(Application.Current?.MainWindow, "That file no longer exists.", "Graphite",
                DialogButtons.OK, DialogIcon.Info);
            _ = ReloadAsync(rescanRail: true);
            return;
        }
        if (OpenWithWindows(path)) return;
        await _main.OpenFilesAsync(new[] { path }, resume: true);
    }

    /// <summary>Files Graphite can't show (images, text, archives…) go to whatever app Windows
    /// has for them. Returns true when the file was handled that way.</summary>
    private static bool OpenWithWindows(string path)
    {
        if (LibraryScanner.IsSupported(path, includeOffice: true)) return false;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageDialog.Show(Application.Current?.MainWindow, $"Windows couldn't open this file.\n{ex.Message}",
                "Graphite", DialogButtons.OK, DialogIcon.Info);
        }
        return true;
    }

    /// <summary>Open in a background tab and stay in the library, to queue several files.</summary>
    [RelayCommand]
    private async Task OpenFileInBackground(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (OpenWithWindows(path)) return;
        await _main.OpenFilesAsync(new[] { path }, activate: false, resume: true);
    }

    /// <summary>Word files: open in Graphite's editor (a copy, when Graphite didn't write it).</summary>
    [RelayCommand]
    private Task OpenAsEditor(string? path) => OpenWord(path, inEditor: true);

    /// <summary>Word files: convert to PDF and open that.</summary>
    [RelayCommand]
    private Task OpenAsPdf(string? path) => OpenWord(path, inEditor: false);

    private async Task OpenWord(string? path, bool inEditor)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path))
        {
            MessageDialog.Show(Application.Current?.MainWindow, "That file no longer exists.", "Graphite",
                DialogButtons.OK, DialogIcon.Info);
            _ = ReloadAsync(rescanRail: true);
            return;
        }
        await _main.OpenWordAsync(path, inEditor);
    }

    [RelayCommand]
    private Task Resume() => OpenFile(ContinueItem?.Path);

    [RelayCommand]
    private void OpenFileDialog() => _main.OpenCommand.Execute(null);

    [RelayCommand]
    private void TogglePin(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        bool pinned = ThemeService.TogglePinned(path);
        foreach (var card in Files.Concat(Recents).Where(c => LibraryScanner.SamePath(c.Path, path)))
            card.IsPinned = pinned;
        if (CurrentView == LibraryView.Pinned) _ = ReloadAsync(rescanRail: false);
    }

    [RelayCommand]
    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) { App.LogError("Show in Explorer failed", ex); }
    }

    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        ClipboardHelper.Try(() => { Clipboard.SetText(path); return true; });
    }

    // ------------------------------------------------------------- loading

    /// <summary>Rebuild whatever the current view shows. <paramref name="rescanRail"/> re-reads
    /// the root's sub-folders (needed after a change on disk); plain navigation reuses them.</summary>
    private async Task ReloadAsync(bool rescanRail)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        IsLoading = true;
        try
        {
            string? root = RootPath;
            bool office = IncludeOffice;

            if (root != null && (rescanRail || !_railLoaded))
            {
                if (!Directory.Exists(root))
                {
                    // The folder went away (renamed, drive unplugged): fall back to the empty state.
                    _roots.RemoveAll(r => LibraryScanner.SamePath(r, root));
                    ActivateRoot(_roots.FirstOrDefault(), save: true);
                    return;
                }
                var (folders, rootFiles) = await Task.Run(
                    () => (LibraryScanner.ScanSubfolders(root, office, ct), LibraryScanner.ScanFiles(root, office).Count), ct);
                ct.ThrowIfCancellationRequested();
                _rootFolders = folders;
                _rootFileCount = rootFiles;
                _railLoaded = true;
                BuildRail();
            }

            string query = SearchText.Trim();
            switch (CurrentView)
            {
                case LibraryView.Home when query.Length == 0:
                    BuildHome(ct);
                    break;
                case LibraryView.Home when root != null:
                    await ShowSearchAsync(root, root, query, office, ct);
                    break;
                case LibraryView.Home:
                    await ShowListAsync(RecentPaths(), query, "Home", ct);
                    break;
                case LibraryView.Folder:
                    await LoadFolderAsync(query, office, ct);
                    break;
                case LibraryView.Recent:
                    Title = "Recent";
                    await ShowListAsync(RecentPaths(), query, "Recent", ct);
                    break;
                case LibraryView.Pinned:
                    Title = "Pinned";
                    await ShowListAsync(ThemeService.PinnedFiles.ToList(), query, "Pinned", ct);
                    break;
            }

            ct.ThrowIfCancellationRequested();
            _lastRefreshUtc = DateTime.UtcNow;
            UpdateStatus();
            OnPropertyChanged(nameof(SearchPlaceholder));
        }
        catch (OperationCanceledException) { /* a newer request took over */ }
        catch (Exception ex) { App.LogError("Library refresh failed", ex); }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsLoading = false;
        }
    }

    private static List<string> RecentPaths() => ThemeService.RecentFiles.ToList();

    // ---- home

    private void BuildHome(CancellationToken ct)
    {
        Files.Clear();
        HasFiles = false;
        Breadcrumbs.Clear();

        if (RootPath == null)
        {
            Title = "Home";
            Subtitle = "Choose a main folder and its PDFs show up here.";
            Folders.Clear();
            HasFolders = false;
        }
        else
        {
            Title = RootName;
            int pdfs = _rootFolders.Sum(f => f.PdfCount) + _rootFileCount;
            Subtitle = $"{LibraryScanner.Plural(_rootFolders.Count, "folder")} · {LibraryScanner.Plural(pdfs, IncludeOffice ? "file" : "PDF")}";

            IEnumerable<LibraryFolder> sorted = SortMode switch
            {
                "Modified" => _rootFolders.OrderByDescending(f => f.NewestWriteUtc),
                "Size" => _rootFolders.OrderByDescending(f => f.PdfCount),
                _ => _rootFolders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
            };
            Folders.Clear();
            foreach (var f in sorted) Folders.Add(new FolderCardViewModel(f, ct));
            HasFolders = Folders.Count > 0;
        }

        BuildRecentsAndContinue();
        UpdateRailSelection();
    }

    private void BuildRecentsAndContinue()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        Recents.Clear();
        FileCardViewModel? resume = null;
        foreach (var path in ThemeService.RecentFiles)
        {
            if (Recents.Count >= 8 && resume != null) break;
            var info = TryGetFile(path);
            if (info == null) continue;
            var card = new FileCardViewModel(info, ct);
            if (Recents.Count < 8) Recents.Add(card);
            // The newest file that has been started but not finished is what "continue reading" offers.
            if (resume == null && card.ReadingPage > 0 && card.PageCount > 0 && card.ReadingPage < card.PageCount - 1)
                resume = new FileCardViewModel(info, ct);
        }
        HasRecents = Recents.Count > 0;
        ContinueItem = resume;
    }

    // ---- folder view

    private async Task LoadFolderAsync(string query, bool office, CancellationToken ct)
    {
        string? path = CurrentPath;
        if (path == null || !Directory.Exists(path))
        {
            ShowHome();
            return;
        }

        Title = LibraryScanner.SamePath(path, RootPath ?? "") ? "Files in this folder" : FolderName(path);
        BuildBreadcrumbs(path);
        bool all = ShowAllTypes;

        if (query.Length > 0)
        {
            await ShowSearchAsync(path, path, query, office, ct, allTypes: all);
            return;
        }

        var (files, subs) = await Task.Run(
            () => (LibraryScanner.ScanFiles(path, office, all), LibraryScanner.ScanSubfolders(path, office, ct)), ct);
        ct.ThrowIfCancellationRequested();

        Folders.Clear();
        foreach (var f in subs) Folders.Add(new FolderCardViewModel(f, ct));
        HasFolders = Folders.Count > 0;

        FillFiles(SortFiles(files), ct);
        int online = files.Count(f => f.OnlineOnly);
        string noun = office || all ? "file" : "PDF";
        Subtitle = $"{LibraryScanner.Plural(files.Count, noun)} · {LibraryScanner.Plural(subs.Count, "subfolder")}" +
                   (online > 0 ? $" · {online} online only" : "");
        EmptyText = subs.Count > 0 ? "No files directly in this folder — open one of the folders above."
                                   : all ? "Nothing in this folder yet." : "No PDFs in this folder yet.";
    }

    private void BuildBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();
        if (RootPath == null || !LibraryScanner.IsSameOrChild(RootPath, path))
        {
            Breadcrumbs.Add(new BreadcrumbItem(FolderName(path), path, true));
            return;
        }

        var chain = new List<(string Name, string Path)> { (RootName, RootPath) };
        string relative = Path.GetRelativePath(RootPath, path);
        if (relative != ".")
        {
            string walk = RootPath;
            foreach (var part in relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                walk = Path.Combine(walk, part);
                chain.Add((part, walk));
            }
        }
        // "Files in this folder" at the root is just the root itself.
        for (int i = 0; i < chain.Count; i++)
            Breadcrumbs.Add(new BreadcrumbItem(chain[i].Name, chain[i].Path, i == chain.Count - 1));
    }

    // ---- search & flat lists

    private async Task ShowSearchAsync(string scopePath, string rootForTitle, string query, bool office,
        CancellationToken ct, bool allTypes = false)
    {
        var hits = await Task.Run(() => LibraryScanner.Search(scopePath, query, office, ct, allTypes: allTypes), ct);
        ct.ThrowIfCancellationRequested();
        Folders.Clear();
        HasFolders = false;
        FillFiles(hits, ct);
        Breadcrumbs.Clear();
        if (CurrentView == LibraryView.Folder && CurrentPath != null) BuildBreadcrumbs(CurrentPath);
        Title = CurrentView == LibraryView.Folder ? Title : "Search results";
        Subtitle = $"{LibraryScanner.Plural(hits.Count, "match", "matches")} in {FolderName(rootForTitle)}";
        EmptyText = $"No files match “{query}”.";
    }

    private async Task ShowListAsync(List<string> paths, string query, string what, CancellationToken ct)
    {
        var found = await Task.Run(() =>
        {
            var list = new List<LibraryFile>();
            foreach (var p in paths)
            {
                ct.ThrowIfCancellationRequested();
                var info = TryGetFile(p);
                if (info != null && (query.Length == 0 ||
                    query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                         .All(t => Path.GetFileNameWithoutExtension(info.Name).Contains(t, StringComparison.OrdinalIgnoreCase))))
                    list.Add(info);
            }
            return list;
        }, ct);
        ct.ThrowIfCancellationRequested();

        Folders.Clear();
        HasFolders = false;
        Breadcrumbs.Clear();
        FillFiles(found, ct);

        if (query.Length > 0)
        {
            Title = what == "Home" ? "Search results" : Title;
            Subtitle = $"{LibraryScanner.Plural(found.Count, "match", "matches")}";
            EmptyText = $"No files match “{query}”.";
        }
        else
        {
            Subtitle = LibraryScanner.Plural(found.Count, "file");
            EmptyText = what == "Pinned"
                ? "Nothing pinned yet. Use the ⋯ menu on a file to pin it here."
                : "Nothing opened yet. Files you open show up here.";
        }
    }

    // ---- helpers

    private static LibraryFile? TryGetFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            return new LibraryFile(fi.FullName, fi.Name, fi.DirectoryName ?? "", fi.Length, fi.LastWriteTimeUtc,
                LibraryScanner.IsOnlineOnly(fi.Attributes), LibraryScanner.IsOffice(fi.Name));
        }
        catch { return null; }
    }

    private List<LibraryFile> SortFiles(IEnumerable<LibraryFile> files) => SortMode switch
    {
        "Modified" => files.OrderByDescending(f => f.ModifiedUtc).ToList(),
        "Size" => files.OrderByDescending(f => f.Size).ToList(),
        _ => files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
    };

    private void FillFiles(IEnumerable<LibraryFile> files, CancellationToken ct)
    {
        Files.Clear();
        foreach (var f in files)
        {
            var card = new FileCardViewModel(f, ct);
            card.PropertyChanged += Card_PropertyChanged;
            Files.Add(card);
        }
        HasFiles = Files.Count > 0;
    }

    private static string FolderName(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    private static string RootSubtitle(string path)
    {
        string? parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
        string label = LibraryScanner.IsUnderOneDrive(path) ? "OneDrive" : "This PC";
        string parentName = parent == null ? "" : FolderName(parent);
        return parentName.Length > 0 ? $"{label} · {parentName}" : label;
    }

    private void BuildRail()
    {
        RailItems.Clear();
        foreach (var root in _roots)
        {
            RailItems.Add(new RailItem(RailKind.Root, FolderName(root), root, RootSubtitle(root)));
            if (RootPath == null || !LibraryScanner.SamePath(root, RootPath)) continue;

            foreach (var f in _rootFolders)
                RailItems.Add(new RailItem(RailKind.Folder, f.Name, f.Path, countText: f.PdfCount.ToString()));
            if (_rootFileCount > 0)
                RailItems.Add(new RailItem(RailKind.Files, "Files in this folder", root, countText: _rootFileCount.ToString()));
        }
        UpdateRailSelection();
    }

    private void UpdateRailSelection()
    {
        foreach (var item in RailItems)
        {
            item.IsActive = item.Kind switch
            {
                // Folded away, the main folder stands in for everything inside it.
                RailKind.Root => RootPath != null && LibraryScanner.SamePath(item.Path, RootPath) &&
                                 (CurrentView == LibraryView.Home ||
                                  (!IsRailExpanded && CurrentView == LibraryView.Folder)),
                RailKind.Files => CurrentView == LibraryView.Folder && CurrentPath != null &&
                                  LibraryScanner.SamePath(CurrentPath, item.Path),
                _ => CurrentView == LibraryView.Folder && CurrentPath != null &&
                     LibraryScanner.IsSameOrChild(item.Path, CurrentPath),
            };
        }
    }

    partial void OnIsRailExpandedChanged(bool value) => UpdateRailSelection();

    partial void OnShowAllTypesChanged(bool value)
    {
        if (CurrentView == LibraryView.Folder) _ = ReloadAsync(rescanRail: false);
    }

    [RelayCommand]
    private void ToggleRail() => IsRailExpanded = !IsRailExpanded;

    private void UpdateStatus() =>
        StatusText = HasRoot ? $"Synced {LibraryScanner.Ago(_lastRefreshUtc)}" : "";
}

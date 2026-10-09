using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core.Library;
using Graphite.Core.Pdf;
using Microsoft.Win32;

namespace Graphite.App.ViewModels;

/// <summary>
/// The start screen's "workspace" side: the clock and greeting, creating things (documents,
/// folders), back/forward history, and acting on several ticked files at once.
/// </summary>
public partial class LibraryViewModel
{
    // ------------------------------------------------------------- clock & greeting

    private int _clockTicks;

    /// <summary>The hour part of the clock: "2" or "14", following the person's regional settings.
    /// Hour and minute are separate so the start screen can draw its own accent-colored colon.</summary>
    [ObservableProperty] private string clockHour = "";

    /// <summary>The minute part of the clock: "41".</summary>
    [ObservableProperty] private string clockMinute = "";

    /// <summary>"PM" when the region uses a 12-hour clock, otherwise empty.</summary>
    [ObservableProperty] private string clockSuffix = "";

    [ObservableProperty] private string greeting = "";
    [ObservableProperty] private string dateText = "";

    private void OnClockTick()
    {
        UpdateClock();
        if (++_clockTicks % 30 == 0) UpdateStatus();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        bool twelveHour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t');
        ClockHour = now.ToString(twelveHour ? "%h" : "HH", CultureInfo.CurrentCulture);
        ClockMinute = now.ToString("mm", CultureInfo.CurrentCulture);
        ClockSuffix = twelveHour ? now.ToString("tt", CultureInfo.CurrentCulture) : "";
        DateText = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        Greeting = now.Hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 23 => "Good evening",
            _ => "Hello",
        };
    }

    // ------------------------------------------------------------- where new things go

    /// <summary>New documents and folders are created in the folder being looked at, otherwise in
    /// the library's main folder, otherwise in Documents.</summary>
    public string NewItemFolder
    {
        get
        {
            if (CurrentView == LibraryView.Folder && CurrentPath != null && Directory.Exists(CurrentPath)) return CurrentPath;
            if (RootPath != null && Directory.Exists(RootPath)) return RootPath;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    public string NewItemFolderName => FolderName(NewItemFolder);
    public string NewItemHint => $"New items are saved in {NewItemFolderName}";

    /// <summary>The location pill in the toolbar: "Home · Folder", "Recent", "Folder › Sub".</summary>
    public string LocationLabel
    {
        get
        {
            switch (CurrentView)
            {
                case LibraryView.Recent: return "Recent";
                case LibraryView.Pinned: return "Pinned";
                case LibraryView.Folder when CurrentPath != null:
                    if (RootPath != null && LibraryScanner.IsSameOrChild(RootPath, CurrentPath))
                    {
                        string relative = Path.GetRelativePath(RootPath, CurrentPath);
                        if (relative == ".") return RootName;
                        var parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                        return RootName + " › " + string.Join(" › ", parts);
                    }
                    return FolderName(CurrentPath);
                default:
                    return HasRoot ? $"Home · {RootName}" : "Home";
            }
        }
    }

    private void OnLocationChanged()
    {
        OnPropertyChanged(nameof(LocationLabel));
        OnPropertyChanged(nameof(NewItemFolder));
        OnPropertyChanged(nameof(NewItemFolderName));
        OnPropertyChanged(nameof(NewItemHint));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    // ------------------------------------------------------------- creating

    [RelayCommand]
    private void NewDocument() => _main.NewDocument();

    [RelayCommand]
    private void NewFolder()
    {
        var owner = Application.Current?.MainWindow;
        if (owner == null) return;

        string parent = NewItemFolder;
        string? entered = InputDialog.Show(owner, "New folder", $"Name of the new folder in {FolderName(parent)}", "New folder");
        if (entered == null) return;

        string name = GraphiteDocx.SanitizeFileName(entered);
        string path = Path.Combine(parent, name);
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                MessageDialog.Show(owner, $"“{name}” already exists in {FolderName(parent)}.", "Graphite",
                    DialogButtons.OK, DialogIcon.Info);
                return;
            }
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            App.LogError("Creating a folder failed", ex);
            MessageDialog.Show(owner, $"Couldn't create the folder.\n{ex.Message}", "Graphite",
                DialogButtons.OK, DialogIcon.Warning);
            return;
        }
        _ = ReloadAsync(rescanRail: true);
    }

    // ------------------------------------------------------------- back / forward

    private readonly record struct NavEntry(LibraryView View, string? Folder);

    private readonly List<NavEntry> _history = new();
    private int _historyIndex = -1;
    private bool _navigating;

    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    private void RecordNav()
    {
        if (!_navigating)
        {
            var entry = new NavEntry(CurrentView, CurrentView == LibraryView.Folder ? CurrentPath : null);
            if (_historyIndex < 0 || _history[_historyIndex] != entry)
            {
                if (_historyIndex < _history.Count - 1)
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(entry);
                _historyIndex = _history.Count - 1;
                if (_history.Count > 60)
                {
                    _history.RemoveAt(0);
                    _historyIndex--;
                }
            }
        }
        OnLocationChanged();
    }

    [RelayCommand] private void GoBack() => Travel(-1);
    [RelayCommand] private void GoForward() => Travel(+1);

    private void Travel(int step)
    {
        int target = _historyIndex + step;
        if (target < 0 || target >= _history.Count) return;
        _historyIndex = target;
        var entry = _history[target];
        _navigating = true;
        try
        {
            switch (entry.View)
            {
                case LibraryView.Recent: ShowRecent(); break;
                case LibraryView.Pinned: ShowPinned(); break;
                case LibraryView.Folder when entry.Folder != null && Directory.Exists(entry.Folder):
                    ShowFolder(entry.Folder);
                    break;
                default: ShowHome(); break;
            }
        }
        finally { _navigating = false; }
        OnLocationChanged();
    }

    // ------------------------------------------------------------- left pane

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailColumnWidth))]
    private bool isRailVisible = true;

    /// <summary>The rail column includes the gap to the content, so hiding it leaves no seam.</summary>
    public GridLength RailColumnWidth => IsRailVisible ? new GridLength(286) : new GridLength(0);

    [RelayCommand]
    private void ToggleRailPane() => IsRailVisible = !IsRailVisible;

    // ------------------------------------------------------------- selection

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    [NotifyPropertyChangedFor(nameof(PinSelectionText))]
    private int selectedCount;

    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => $"{SelectedCount} selected";

    /// <summary>Two or more PDFs, nothing else, none waiting in the cloud.</summary>
    public bool CanMerge
    {
        get
        {
            var picked = SelectedCards();
            return picked.Count >= 2 && picked.All(c => c.IsPdf && !c.OnlineOnly);
        }
    }

    public string PinSelectionText => SelectedCards().All(c => c.IsPinned) && SelectedCount > 0 ? "Unpin" : "Pin";

    private List<FileCardViewModel> SelectedCards() => Files.Where(c => c.IsSelected).ToList();

    private void Card_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileCardViewModel.IsSelected)) RecountSelection();
        else if (e.PropertyName == nameof(FileCardViewModel.IsPinned) && HasSelection)
            OnPropertyChanged(nameof(PinSelectionText));
    }

    private void RecountSelection()
    {
        int count = Files.Count(c => c.IsSelected);
        if (count == SelectedCount)
        {
            if (count > 0) OnPropertyChanged(nameof(CanMerge));
            return;
        }
        SelectedCount = count;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var card in Files) card.IsSelected = false;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var card in Files) card.IsSelected = true;
    }

    private static Window? Owner => Application.Current?.MainWindow;

    private bool IsOpenInTab(string path) =>
        _main.Documents.Any(d => d.FilePath != null && LibraryScanner.SamePath(d.FilePath, path)) ||
        _main.Writers.Any(w => LibraryScanner.SamePath(w.FilePath, path));

    /// <summary>Join the ticked PDFs, in the order shown, into one new "Merged.pdf" next to the first.</summary>
    [RelayCommand]
    private async Task MergeSelected()
    {
        var picked = SelectedCards().Where(c => c.IsPdf && !c.OnlineOnly).ToList();
        if (picked.Count < 2) return;

        string folder = Path.GetDirectoryName(picked[0].Path) ?? NewItemFolder;
        string target = GraphiteDocx.UniquePath(folder, "Merged", ".pdf");
        var paths = picked.Select(c => c.Path).ToList();
        try
        {
            await Task.Run(() => File.WriteAllBytes(target, PageOperations.MergeFiles(paths)));
        }
        catch (Exception ex)
        {
            App.LogError("Merging selected files failed", ex);
            MessageDialog.Show(Owner, $"Couldn't merge these files.\n{ex.Message}", "Graphite",
                DialogButtons.OK, DialogIcon.Warning);
            return;
        }
        ClearSelection();
        _ = ReloadAsync(rescanRail: true);
        await _main.OpenFilesAsync(new[] { target });
    }

    /// <summary>Move the ticked files into a folder chosen in the system picker.</summary>
    [RelayCommand]
    private void MoveSelected()
    {
        var picked = SelectedCards();
        if (picked.Count == 0) return;

        var dialog = new OpenFolderDialog
        {
            Title = picked.Count == 1 ? "Move to…" : $"Move {picked.Count} files to…",
            InitialDirectory = RootPath ?? NewItemFolder,
        };
        if (dialog.ShowDialog(Owner) != true) return;
        string destination = dialog.FolderName;

        int moved = 0;
        var skipped = new List<string>();
        foreach (var card in picked)
        {
            if (IsOpenInTab(card.Path)) { skipped.Add(card.FileName + " (open in a tab)"); continue; }
            string? dir = Path.GetDirectoryName(card.Path);
            if (dir != null && LibraryScanner.SamePath(dir, destination)) continue;
            string target = Path.Combine(destination, card.FileName);
            if (File.Exists(target)) { skipped.Add(card.FileName + " (already there)"); continue; }
            try
            {
                File.Move(card.Path, target);
                ThemeService.RenamePath(card.Path, target);
                GraphiteDocx.Forget(card.Path);
                moved++;
            }
            catch (Exception ex)
            {
                App.LogError("Moving a file failed", ex);
                skipped.Add(card.FileName + " (" + ex.Message + ")");
            }
        }

        ClearSelection();
        _ = ReloadAsync(rescanRail: true);
        if (skipped.Count > 0)
            MessageDialog.Show(Owner,
                $"Moved {moved} of {picked.Count}. Not moved:\n" + string.Join("\n", skipped.Take(8)),
                "Graphite", DialogButtons.OK, DialogIcon.Info);
    }

    [RelayCommand]
    private void PinSelected()
    {
        var picked = SelectedCards();
        if (picked.Count == 0) return;
        bool pin = picked.Any(c => !c.IsPinned);
        foreach (var card in picked)
            if (card.IsPinned != pin) TogglePin(card.Path);
        OnPropertyChanged(nameof(PinSelectionText));
    }

    [RelayCommand]
    private void DeleteSelected() => DeleteCards(SelectedCards());

    /// <summary>"Delete" on a single file's menu: just that file — or everything ticked, when
    /// the file is one of several ticked ones.</summary>
    [RelayCommand]
    private void DeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var card = Files.FirstOrDefault(c => LibraryScanner.SamePath(c.Path, path));
        if (card == null) return;
        var ticked = SelectedCards();
        DeleteCards(card.IsSelected && ticked.Count > 1 ? ticked : new List<FileCardViewModel> { card });
    }

    private void DeleteCards(List<FileCardViewModel> picked)
    {
        if (picked.Count == 0) return;

        var open = picked.Where(c => IsOpenInTab(c.Path)).ToList();
        if (open.Count > 0)
        {
            MessageDialog.Show(Owner,
                $"Close {(open.Count == 1 ? "“" + open[0].FileName + "”" : "the open files")} first, then delete.",
                "Graphite", DialogButtons.OK, DialogIcon.Info);
            return;
        }

        string question = picked.Count == 1
            ? $"Move “{picked[0].FileName}” to the Recycle Bin?"
            : $"Move {picked.Count} files to the Recycle Bin?";
        if (MessageDialog.Show(Owner, question, "Graphite", DialogButtons.YesNo, DialogIcon.Warning) != MessageBoxResult.Yes)
            return;

        var failed = new List<string>();
        foreach (var card in picked)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(card.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                GraphiteDocx.Forget(card.Path);
            }
            catch (Exception ex)
            {
                App.LogError("Deleting a file failed", ex);
                failed.Add(card.FileName);
            }
        }
        ClearSelection();
        _ = ReloadAsync(rescanRail: true);
        if (failed.Count > 0)
            MessageDialog.Show(Owner, "Couldn't delete:\n" + string.Join("\n", failed.Take(8)), "Graphite",
                DialogButtons.OK, DialogIcon.Warning);
    }

    /// <summary>Bundle the ticked files into one .zip (for sending several at once).</summary>
    [RelayCommand]
    private void ZipSelected()
    {
        var picked = SelectedCards().Where(c => !c.OnlineOnly).ToList();
        if (picked.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Zip archive|*.zip",
            FileName = picked.Count == 1 ? Path.GetFileNameWithoutExtension(picked[0].FileName) + ".zip" : "Selected files.zip",
            InitialDirectory = NewItemFolder,
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
            using var zip = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in picked)
            {
                string entry = card.FileName;
                for (int n = 2; !used.Add(entry); n++)
                    entry = $"{Path.GetFileNameWithoutExtension(card.FileName)} ({n}){Path.GetExtension(card.FileName)}";
                zip.CreateEntryFromFile(card.Path, entry, CompressionLevel.Optimal);
            }
        }
        catch (Exception ex)
        {
            App.LogError("Zipping files failed", ex);
            MessageDialog.Show(Owner, $"Couldn't create the zip.\n{ex.Message}", "Graphite",
                DialogButtons.OK, DialogIcon.Warning);
            return;
        }
        _ = ReloadAsync(rescanRail: true);
    }

    [RelayCommand]
    private void CopySelectedPaths()
    {
        var picked = SelectedCards();
        if (picked.Count == 0) return;
        string text = string.Join(Environment.NewLine, picked.Select(c => c.Path));
        ClipboardHelper.Try(() => { Clipboard.SetText(text); return true; });
    }
}

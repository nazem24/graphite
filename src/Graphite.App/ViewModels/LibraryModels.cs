using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.App.Services;
using Graphite.Core.Library;

namespace Graphite.App.ViewModels;

/// <summary>Which page of the start screen is showing.</summary>
public enum LibraryView { Home, Folder, Recent, Pinned }

public enum RailKind { Root, Folder, Files }

/// <summary>A folder: a folder-shaped card on the library home and inside other folders, a row in
/// list view. Its colour is the person's own choice (see <see cref="FolderPalette"/>).</summary>
public sealed partial class FolderCardViewModel : ObservableObject
{
    private readonly LibraryFolder _folder;
    private FolderLook _look;

    // The token is part of the signature so every place that builds cards stays as it was.
    public FolderCardViewModel(LibraryFolder folder, CancellationToken ct)
    {
        _folder = folder;
        _look = FolderPalette.Look(ReadColor());
    }

    public string Name => _folder.Name;
    public string Path => _folder.Path;
    public int PdfCount => _folder.PdfCount;
    public DateTime NewestWriteUtc => _folder.NewestWriteUtc;
    public string CountText => LibraryScanner.Plural(PdfCount, "PDF");
    /// <summary>"7 PDFs", or "No files yet".</summary>
    public string CountLine => PdfCount == 0 ? "No files yet" : CountText;

    /// <summary>"Changed 5 months ago", empty for an empty folder.</summary>
    public string ChangedText => PdfCount == 0 ? "" : $"Changed {LibraryScanner.Changed(_folder.NewestWriteUtc)}";

    /// <summary>The second line of the card: when it last changed, or that it is empty.</summary>
    public string SubText => PdfCount == 0 ? "No files yet" : ChangedText;

    public string Detail => PdfCount == 0
        ? "No files yet"
        : $"{CountText} · changed {LibraryScanner.Changed(_folder.NewestWriteUtc)}";

    /// <summary>The number in the round badge on the pocket.</summary>
    public bool ShowBadge => PdfCount > 0;
    public string BadgeText => PdfCount > 99 ? "99+" : PdfCount.ToString();

    // ---- colour

    public Brush BackBrush => _look.Back;
    public Brush FrontBrush => _look.Front;
    public Brush TextBrush => _look.Text;
    public Brush SubTextBrush => _look.SubText;
    public Brush BadgeBrush => _look.Badge;
    public Brush BadgeTextBrush => _look.BadgeText;
    public Brush BarBrush => _look.Bar;
    public Brush BarLightBrush => _look.BarLight;

    /// <summary>The pocket colour as "#RRGGBB" (what the colour picker shows as current).</summary>
    public string ColorHex => FolderPalette.ToHex(ReadColor());

    /// <summary>The folder's own colour, else the all-folders colour, else the built-in teal.</summary>
    private Color ReadColor() =>
        FolderPalette.TryParse(ThemeService.GetFolderColor(Path), out var c) ? c : FolderPalette.Default;

    /// <summary>The saved colour changed: read it again and redraw the card.</summary>
    public void RefreshColor()
    {
        _look = FolderPalette.Look(ReadColor());
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One document in the library: a card (grid) or a row (list).</summary>
public sealed partial class FileCardViewModel : ObservableObject
{
    private readonly CancellationToken _ct;
    private bool _thumbRequested;
    private int _readingPage;

    public FileCardViewModel(LibraryFile file, CancellationToken ct)
    {
        _ct = ct;
        Path = file.Path;
        FileName = file.Name;
        FolderName = System.IO.Path.GetFileName(file.FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : file.FolderPath;
        Size = file.Size;
        ModifiedUtc = file.ModifiedUtc;
        OnlineOnly = file.OnlineOnly;
        IsOffice = file.IsOffice;
        Extension = System.IO.Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant();
        RefreshReading();
    }

    public string Path { get; }
    public string FileName { get; }
    public string FolderName { get; }
    public long Size { get; }
    public DateTime ModifiedUtc { get; }
    public bool OnlineOnly { get; }
    public bool IsOffice { get; }
    public string Extension { get; }

    /// <summary>Not a PDF and not an Office file: shown by "all file types", opened by Windows.</summary>
    public bool IsOther => !IsOffice && !Extension.Equals("PDF", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Word .docx: gets separate "Edit" and "PDF" buttons.</summary>
    public bool IsDocx => Extension.Equals("DOCX", StringComparison.OrdinalIgnoreCase);

    /// <summary>A PDF (what Merge works on).</summary>
    public bool IsPdf => Extension.Equals("PDF", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ticked in the list: the toolbar then offers actions for everything ticked.</summary>
    [ObservableProperty] private bool isSelected;

    /// <summary>Files Graphite can't draw a page of get their extension on a blank sheet instead.</summary>
    public bool ShowExtension => IsOffice || IsOther;

    /// <summary>When it was last opened in Graphite, if ever.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenText))]
    [NotifyPropertyChangedFor(nameof(RecentText))]
    [NotifyPropertyChangedFor(nameof(ContinueText))]
    private DateTime? openedUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoText))]
    [NotifyPropertyChangedFor(nameof(ContinueText))]
    [NotifyPropertyChangedFor(nameof(PageText))]
    private int pageCount;

    [ObservableProperty] private ImageSource? thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinMenuText))]
    private bool isPinned;

    /// <summary>0..1 share of the document already read; 0 when not started.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    [NotifyPropertyChangedFor(nameof(ProgressDone))]
    [NotifyPropertyChangedFor(nameof(ProgressRest))]
    private double progress;

    public bool HasProgress => Progress > 0.001 && Progress < 0.999;
    // The progress bar is a two-column star grid: done | rest.
    public GridLength ProgressDone => new(Math.Max(Progress, 0.0001), GridUnitType.Star);
    public GridLength ProgressRest => new(Math.Max(1 - Progress, 0.0001), GridUnitType.Star);

    public string PinMenuText => IsPinned ? "Unpin" : "Pin";

    /// <summary>"24 pages · 2.1 MB", "Online only · 9.4 MB", "DOCX · 310 KB".</summary>
    public string InfoText
    {
        get
        {
            string size = LibraryScanner.FormatSize(Size);
            if (OnlineOnly) return $"Online only · {size}";
            if (IsOffice || IsOther) return $"{(Extension.Length > 0 ? Extension : "File")} · {size}";
            return PageCount > 0 ? $"{LibraryScanner.Plural(PageCount, "page")} · {size}" : size;
        }
    }

    /// <summary>"Opened 2 h ago" when it has been read here, else "Modified 3 Oct".</summary>
    public string WhenText => OpenedUtc is { } t
        ? $"Opened {LibraryScanner.Ago(t)}"
        : $"Modified {LibraryScanner.ShortDate(ModifiedUtc)}";

    /// <summary>"CPE · 2 h ago": a recent file always names its folder, so two files called
    /// "Summary" are never ambiguous.</summary>
    public string RecentText => $"{FolderName} · {LibraryScanner.Ago(OpenedUtc ?? ModifiedUtc)}";

    /// <summary>"in CPE · page 14 of 24 · 2 h ago".</summary>
    public string ContinueText
    {
        get
        {
            string where = PageCount > 0 ? $" · page {_readingPage + 1} of {PageCount}" : "";
            string when = OpenedUtc is { } t ? $" · {LibraryScanner.Ago(t)}" : "";
            return $"in {FolderName}{where}{when}";
        }
    }

    /// <summary>"p. 12 / 48": where the person stopped, short enough for the small continue card.</summary>
    public string PageText => PageCount > 0 ? $"p. {_readingPage + 1} / {PageCount}" : "";

    /// <summary>The page the person stopped on (0-based), or -1 when never opened.</summary>
    public int ReadingPage => OpenedUtc == null ? -1 : _readingPage;

    /// <summary>Re-read where the person stopped and whether the file is pinned
    /// (called when the start screen comes back after reading).</summary>
    public void RefreshReading()
    {
        IsPinned = ThemeService.IsPinned(Path);
        var info = ThemeService.GetReading(Path);
        if (info == null) return;
        _readingPage = info.Page;
        if (info.PageCount > 0) PageCount = info.PageCount;
        OpenedUtc = info.LastOpenedUtc;
        Progress = info.PageCount > 1 && info.Page > 0 ? (info.Page + 1.0) / info.PageCount : 0;
        OnPropertyChanged(nameof(ContinueText));
        OnPropertyChanged(nameof(PageText));
    }

    /// <summary>Render the first page (skipped for online-only and Office files).</summary>
    public async Task LoadThumbnailAsync()
    {
        if (_thumbRequested || OnlineOnly || IsOffice || IsOther) return;
        _thumbRequested = true;
        var result = await ThumbnailService.GetAsync(Path, Size, ModifiedUtc, 340, _ct);
        if (result == null || _ct.IsCancellationRequested) return;
        Thumbnail = result.Image;
        if (result.PageCount > 0)
        {
            PageCount = result.PageCount;
            if (_readingPage > 0)
                Progress = result.PageCount > 1 ? Math.Min(1, (_readingPage + 1.0) / result.PageCount) : 0;
        }
    }
}

/// <summary>One crumb of the folder path shown above a folder's title.</summary>
public sealed record BreadcrumbItem(string Name, string Path, bool IsLast)
{
    public bool IsNotLast => !IsLast;
}

/// <summary>A row of the left rail: a library root, one of its sub-folders, or its loose files.</summary>
public sealed partial class RailItem : ObservableObject
{
    public RailItem(RailKind kind, string name, string path, string subtitle = "", string countText = "")
    {
        Kind = kind;
        Name = name;
        Path = path;
        Subtitle = subtitle;
        CountText = countText;
    }

    public RailKind Kind { get; }
    public string Name { get; }
    public string Path { get; }
    public string Subtitle { get; }
    public string CountText { get; }

    public bool IsRoot => Kind == RailKind.Root;
    public bool IsFolder => Kind == RailKind.Folder;
    public bool IsFiles => Kind == RailKind.Files;

    [ObservableProperty] private bool isActive;
}

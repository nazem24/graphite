using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using Graphite.App.Services;
using Graphite.Core.Canvas;
using Graphite.Core.Library;

namespace Graphite.App.ViewModels;

/// <summary>Which page of the Canvas space is showing.</summary>
public enum CanvasPage { Connect, Overview, New, Pinned, Course, Search }

/// <summary>A course: a row in the Canvas rail and a card on the overview.</summary>
public sealed partial class CanvasCourseItem : ObservableObject
{
    public CanvasCourseItem(CanvasCourse course)
    {
        Course = course;
        TintBrush = TintFor(course.Id);
    }

    public CanvasCourse Course { get; }
    public string Id => Course.Id;
    public string Name => Course.Name;
    public string Code => Course.Code;

    /// <summary>The big mark behind the title on the course card ("ACA"): the course code when it is a short word, else
    /// worked out from the name, see <see cref="CourseText"/>.</summary>
    public string Abbreviation => CourseText.Abbreviation(Course.Name, Course.Code);

    /// <summary>Font size of <see cref="Abbreviation"/>: the shorter the mark, the larger.</summary>
    public double AbbreviationSize => CourseText.AbbreviationSize(Abbreviation);

    /// <summary>The colour of the course's icon in the rails: one of the folder palette's colours, always the same one
    /// for the same course, so courses can be told apart at a glance (the Local rail tints its folders the same way).</summary>
    public Brush TintBrush { get; }

    private static Brush TintFor(string courseId)
    {
        int hash = 17;
        foreach (char c in courseId) hash = unchecked(hash * 31 + c);
        var presets = FolderPalette.Presets;
        return presets[(hash & int.MaxValue) % presets.Count].Brush;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNew))]
    [NotifyPropertyChangedFor(nameof(NewText))]
    [NotifyPropertyChangedFor(nameof(ShowBusy))]
    private int newCount;

    public bool HasNew => NewCount > 0;
    public string NewText => NewCount == 1 ? "1 new" : $"{NewCount} new";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBusy))]
    private bool isBusy;

    /// <summary>Loading, and no count to show in its place.</summary>
    public bool ShowBusy => IsBusy && !HasNew;
    [ObservableProperty] private bool isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked))]
    [NotifyPropertyChangedFor(nameof(FolderName))]
    private string? folder;

    public bool IsLinked => Folder != null;
    public string FolderName => Folder == null ? "" : System.IO.Path.GetFileName(Folder.TrimEnd('\\', '/'));

    /// <summary>"Updated 5 min ago", "Not loaded yet", "Loading…".</summary>
    [ObservableProperty] private string stateText = "";

    public string Tip => Course.Code.Length > 0 && Course.Code != Course.Name ? $"{Course.Name} ({Course.Code})" : Course.Name;
}

/// <summary>One entry of a course: a file, page, assignment, link… or a sub-header.</summary>
public sealed partial class CanvasRowVm : ObservableObject
{
    public CanvasRowVm(string courseId, CanvasItem item, string? path = null)
    {
        CourseId = courseId;
        Item = item;
        PathText = path ?? "";
    }

    public string CourseId { get; }
    public CanvasItem Item { get; }
    public string Key => Item.Key;
    public string Title => Item.Title;
    public bool IsHeader => Item.Kind == CanvasItemKind.Header;
    public bool IsFile => Item.Kind == CanvasItemKind.File;
    public bool IsLink => !IsFile && !IsHeader && Item.Url.Length > 0;
    public bool IsLocked => Item.Locked;

    /// <summary>Where it sits, in flat lists (New, Pinned, search): "Week 3 › Case study".</summary>
    public string PathText { get; }
    public bool HasPath => PathText.Length > 0;

    /// <summary>Indentation as Canvas has it (one step = 18 px).</summary>
    public Thickness IndentMargin { get; set; }

    /// <summary>Files and links found inside a page, shown under it.</summary>
    public ObservableCollection<CanvasRowVm> Links { get; } = new();
    public bool HasLinks => Links.Count > 0;

    [ObservableProperty] private bool isNew;
    [ObservableProperty] private bool isPinned;

    /// <summary>The course is linked to a folder and this file is already in it.</summary>
    [ObservableProperty] private bool onThisPc;

    /// <summary>The course is linked to a folder (enables "Save to course folder").</summary>
    public bool CanSaveToFolder { get; set; }

    /// <summary>The download facts, once known (some files only get them when they are opened).</summary>
    public CanvasFileInfo? FileInfo
    {
        get => _fileInfo ?? Item.File?.Direct;
        set
        {
            _fileInfo = value;
            OnPropertyChanged(nameof(Meta));
            OnPropertyChanged(nameof(HasMeta));
            OnPropertyChanged(nameof(Badge));
        }
    }
    private CanvasFileInfo? _fileInfo;

    /// <summary>The small type label at the start of the row: "PDF", "PPTX", "PAGE", "TASK"…</summary>
    public string Badge => Item.Kind switch
    {
        CanvasItemKind.File => CanvasNames.Extension(FileInfo?.Name ?? Item.Title) is { Length: > 0 } ext ? ext.ToUpperInvariant() : "FILE",
        CanvasItemKind.Page => "PAGE",
        CanvasItemKind.Assignment => "TASK",
        CanvasItemKind.Quiz => "QUIZ",
        CanvasItemKind.Discussion => "FORUM",
        CanvasItemKind.Link => "LINK",
        CanvasItemKind.Tool => "TOOL",
        _ => "ITEM",
    };

    /// <summary>"2.4 MB · 14 Oct" for files.</summary>
    public string Meta
    {
        get
        {
            if (FileInfo is not { } f) return "";
            var parts = new List<string>();
            if (f.Size is { } s) parts.Add(LibraryScanner.FormatSize(s));
            if (f.UpdatedUtc is { } u) parts.Add(LibraryScanner.ShortDate(u));
            return string.Join(" · ", parts);
        }
    }
    public bool HasMeta => Meta.Length > 0;

    public bool HasDue => Item.DueUtc != null;
    public bool IsOverdue => Item.DueUtc is { } d && d < DateTime.UtcNow;
    public bool IsDueSoon => Item.DueUtc is { } d && d >= DateTime.UtcNow && d - DateTime.UtcNow < TimeSpan.FromDays(3);
    public string DueText => Item.DueUtc is { } d
        ? (IsOverdue ? "Was due " : "Due ") + d.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture)
        : "";

    public string ToolTipText => Item.Kind switch
    {
        CanvasItemKind.File => OnThisPc ? "Open (on this PC)" : "Open in Graphite",
        CanvasItemKind.Header => "",
        _ => "Open in your browser",
    };

    partial void OnOnThisPcChanged(bool value) => OnPropertyChanged(nameof(ToolTipText));
}

/// <summary>A module of a course: a collapsible card with its rows.</summary>
public sealed partial class CanvasModuleVm : ObservableObject
{
    public CanvasModuleVm(CanvasModule model, IEnumerable<CanvasRowVm> rows, bool expanded)
    {
        Model = model;
        foreach (var r in rows) Rows.Add(r);
        isExpanded = expanded;
        FileCount = CanvasNew.ModuleFiles(model).Count;
    }

    public CanvasModule Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name.Length > 0 ? Model.Name : "Module";
    public ObservableCollection<CanvasRowVm> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;

    [ObservableProperty] private bool isExpanded;

    /// <summary>False while the module is far out of view: its rows are not laid out or drawn, and an empty space of
    /// <see cref="PlaceholderHeight"/> stands in for them (see CanvasHome).</summary>
    [ObservableProperty] private bool isRealized = true;
    [ObservableProperty] private double placeholderHeight;

    public int FileCount { get; }
    public bool HasFiles => FileCount > 0;
    public string FileCountText => FileCount == 1 ? "1 file" : $"{FileCount} files";

    public bool HasUnlock => Model.UnlockUtc is { } u && u > DateTime.UtcNow;
    public string UnlockText => Model.UnlockUtc is { } u
        ? "Opens " + u.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture)
        : "";
}

/// <summary>A titled group of rows (New, Pinned and search results are grouped by course).</summary>
public sealed class CanvasSectionVm
{
    public CanvasSectionVm(string title, string courseId, IEnumerable<CanvasRowVm> rows, string more = "")
    {
        Title = title;
        CourseId = courseId;
        foreach (var r in rows) Rows.Add(r);
        MoreText = more;
    }

    public string Title { get; }
    public string CourseId { get; }
    /// <summary>The course's own colour (the "New" cards on the overview carry it).</summary>
    public Brush? TintBrush { get; set; }
    public ObservableCollection<CanvasRowVm> Rows { get; } = new();
    public string MoreText { get; }
    public bool HasMore => MoreText.Length > 0;
}

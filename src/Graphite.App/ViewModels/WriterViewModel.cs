using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Documents;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;
using Graphite.Core.Library;

namespace Graphite.App.ViewModels;

/// <summary>One heading in the outline panel.</summary>
public sealed record OutlineItem(string Text, int Level, double Indent, Paragraph Paragraph)
{
    public System.Windows.Thickness IndentMargin => new(Indent, 0, 0, 0);
    public System.Windows.FontWeight Weight => Level <= 1 ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
}

/// <summary>What the command bar can ask the editor surface to do (implemented by the editor view).</summary>
public interface IWriterSurface
{
    void Undo();
    void Redo();
    void InsertImage();
    void InsertLink();
    void InsertTable(int rows, int columns);
    void TableAction(string action);
    void FocusEditor();
}

/// <summary>
/// An open document in the built-in editor: the rich-text content, where it lives on disk, and
/// everything the tab and the editor chrome show about it. Documents are stored as .docx in the
/// folder they were created in and saved automatically a moment after the last edit, so there
/// is no "unsaved changes" prompt.
/// </summary>
public sealed partial class WriterViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _saveTimer;
    private string _title;
    private bool _tracking;
    private bool _disposed;

    public WriterViewModel(string path, FlowDocument document, PageSettings page)
    {
        FilePath = path;
        Document = document;
        Page = page;
        _title = Path.GetFileNameWithoutExtension(path);
        saveStatus = $"Saved to {FolderName}";

        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
    }

    // ------------------------------------------------------------- state

    public FlowDocument Document { get; }
    public PageSettings Page { get; private set; }
    public string FilePath { get; private set; }

    public string FolderPath => Path.GetDirectoryName(FilePath) ?? "";

    public string FolderName
    {
        get
        {
            string folder = FolderPath.TrimEnd('\\', '/');
            string name = Path.GetFileName(folder);
            return name.Length > 0 ? name : folder;
        }
    }

    public ObservableCollection<OutlineItem> Outline { get; } = new();

    [ObservableProperty] private bool isActive;

    /// <summary>The caret is inside a table (the Insert menu's table items apply).</summary>
    [ObservableProperty] private bool inTable;

    /// <summary>True between an edit and the autosave that follows it (the tab shows a dot).</summary>
    [ObservableProperty] private bool isDirty;

    /// <summary>"Saved to Leadershit", "Saving…", or why saving failed.</summary>
    [ObservableProperty] private string saveStatus;

    [ObservableProperty] private int wordCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeadings))]
    private bool outlineHasItems;

    public bool HasHeadings => OutlineHasItems;

    [ObservableProperty] private bool showOutline = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double zoom = 1.0;

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";

    public string WordCountText => WordCount == 1 ? "1 word" : $"{WordCount} words";

    public string StoredAsText => $"Stored as .docx in {FolderName}";

    partial void OnWordCountChanged(int value) => OnPropertyChanged(nameof(WordCountText));

    partial void OnZoomChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.5, 2.5);
        if (Math.Abs(clamped - value) > 0.0001) Zoom = clamped;
    }

    /// <summary>The file name without ".docx". Editing it renames the file.</summary>
    public string Title
    {
        get => _title;
        set
        {
            TryRename(value ?? "");
            OnPropertyChanged(nameof(Title));   // re-read, so a refused name snaps back
        }
    }

    /// <summary>Raised after the file was renamed on disk: (old path, new path).</summary>
    public event Action<string, string>? PathChanged;

    /// <summary>Paper size or margins changed: the editor re-applies them to its surface.</summary>
    public event Action? PageChanged;

    // ------------------------------------------------------------- create / open

    /// <summary>Make "Untitled.docx" (or "Untitled 2.docx"…) in <paramref name="folder"/> and open it.</summary>
    public static WriterViewModel CreateNew(string folder)
    {
        Directory.CreateDirectory(folder);
        string path = GraphiteDocx.UniquePath(folder, "Untitled", ".docx");
        var page = new PageSettings();
        var document = DocxCodec.NewDocument(page);
        DocxCodec.Save(path, document, page, Path.GetFileNameWithoutExtension(path));
        return new WriterViewModel(path, document, page);
    }

    public static WriterViewModel Open(string path)
    {
        var (document, page) = DocxCodec.Load(path);
        return new WriterViewModel(path, document, page);
    }

    // ------------------------------------------------------------- editing & saving

    /// <summary>The editor calls this once it is on screen: edits before that moment are the
    /// document being loaded, not the person typing.</summary>
    public void BeginTracking()
    {
        _tracking = true;
        RefreshStats();
    }

    /// <summary>The content changed: schedule an autosave.</summary>
    public void MarkChanged()
    {
        if (!_tracking || _disposed) return;
        IsDirty = true;
        SaveStatus = "Editing…";
        _saveTimer.Interval = SaveDelay;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public bool Save()
    {
        if (_disposed) return true;
        _saveTimer.Stop();
        try
        {
            DocxCodec.Save(FilePath, Document, Page, _title);
            IsDirty = false;
            SaveStatus = $"Saved to {FolderName}";
            return true;
        }
        catch (Exception ex)
        {
            App.LogError("Saving a document failed", ex);
            SaveStatus = "Couldn't save — will retry";
            _saveTimer.Interval = RetryDelay;
            _saveTimer.Start();
            return false;
        }
    }

    /// <summary>Write out anything still waiting for its autosave (closing the tab or the app).</summary>
    public bool Flush() => !IsDirty || Save();

    // ------------------------------------------------------------- the editor surface

    /// <summary>Set by the editor view once it exists; the command bar's buttons go through it.</summary>
    public IWriterSurface? Surface { get; set; }

    [RelayCommand] private void Undo() => Surface?.Undo();
    [RelayCommand] private void Redo() => Surface?.Redo();
    [RelayCommand] private void InsertImage() => Surface?.InsertImage();
    [RelayCommand] private void InsertLink() => Surface?.InsertLink();

    /// <summary>"3x4" → a table with 3 rows and 4 columns.</summary>
    [RelayCommand]
    private void InsertTable(string? size)
    {
        var parts = (size ?? "3x3").Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int rows) && int.TryParse(parts[1], out int columns))
            Surface?.InsertTable(Math.Clamp(rows, 1, 30), Math.Clamp(columns, 1, 12));
    }

    /// <summary>"row", "column", "deleteRow", "deleteTable": edits the table the caret is in.</summary>
    [RelayCommand] private void TableAction(string? action) { if (action != null) Surface?.TableAction(action); }

    // ------------------------------------------------------------- page setup

    [RelayCommand]
    public void SetPageSize(string? name)
    {
        Page.SetSize(name ?? "A4");
        AfterPageChange();
    }

    [RelayCommand]
    public void SetPageMargins(string? name)
    {
        Page.SetMargins(name ?? "Normal");
        AfterPageChange();
    }

    private void AfterPageChange()
    {
        DocxCodec.Configure(Document, Page);
        PageChanged?.Invoke();
        OnPropertyChanged(nameof(Page));
        IsDirty = true;
        SaveStatus = "Editing…";
        _saveTimer.Interval = SaveDelay;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ------------------------------------------------------------- rename

    private bool TryRename(string requested)
    {
        string name = GraphiteDocx.SanitizeFileName(requested);
        if (name == _title) return true;

        string newPath = Path.Combine(FolderPath, name + ".docx");
        bool onlyCaseChanged = string.Equals(newPath, FilePath, StringComparison.OrdinalIgnoreCase);
        if (!onlyCaseChanged && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            SaveStatus = $"“{name}” already exists here";
            return false;
        }

        // Make sure the file on disk is complete before it moves.
        if (!Flush()) return false;
        try
        {
            string oldPath = FilePath;
            File.Move(oldPath, newPath);
            FilePath = newPath;
            _title = name;
            GraphiteDocx.Forget(oldPath);
            GraphiteDocx.Forget(newPath);
            OnPropertyChanged(nameof(FilePath));
            SaveStatus = $"Saved to {FolderName}";
            PathChanged?.Invoke(oldPath, newPath);
            return true;
        }
        catch (Exception ex)
        {
            App.LogError("Renaming a document failed", ex);
            SaveStatus = "Couldn't rename the file";
            return false;
        }
    }

    // ------------------------------------------------------------- outline & word count

    /// <summary>Recount the words and rebuild the outline (the editor calls this after a pause in typing).</summary>
    public void RefreshStats()
    {
        WordCount = CountWords(new TextRange(Document.ContentStart, Document.ContentEnd).Text);

        var found = new List<(string Text, int Level, Paragraph P)>();
        foreach (Block block in Document.Blocks)
        {
            if (block is not Paragraph p) continue;
            int level = DocStyles.OutlineLevel(DocStyles.Of(p));
            if (level < 0) continue;
            string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
            if (text.Length == 0) continue;
            found.Add((text, level, p));
        }

        int minLevel = found.Count == 0 ? 0 : found.Min(f => f.Level);
        bool same = found.Count == Outline.Count;
        for (int i = 0; same && i < found.Count; i++)
            same = ReferenceEquals(Outline[i].Paragraph, found[i].P) && Outline[i].Text == found[i].Text &&
                   Outline[i].Level == found[i].Level;
        if (!same)
        {
            Outline.Clear();
            foreach (var (text, level, p) in found)
                Outline.Add(new OutlineItem(text, level, (level - minLevel) * 14, p));
        }
        OutlineHasItems = Outline.Count > 0;
    }

    private static int CountWords(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == '￼') inWord = false;
            else if (!inWord) { inWord = true; words++; }
        }
        return words;
    }

    // ------------------------------------------------------------- tidy up

    public void Dispose()
    {
        if (_disposed) return;
        _saveTimer.Stop();
        _disposed = true;
    }
}

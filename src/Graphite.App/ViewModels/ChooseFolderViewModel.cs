using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.Core.Library;

namespace Graphite.App.ViewModels;

/// <summary>One row of the folder tree in the "choose your main folder" dialog. The tree is a
/// flat list (each row carries its depth) so it styles like every other list in the app.</summary>
public sealed partial class FolderNode : ObservableObject
{
    private readonly string _rootName;
    private readonly string _rootPath;

    public FolderNode(string name, string path, int depth, bool isCloud, string rootName, string rootPath)
    {
        Name = name;
        Path = path;
        Depth = depth;
        IsCloud = isCloud;
        _rootName = rootName;
        _rootPath = rootPath;
    }

    public string Name { get; }
    public string Path { get; }
    public int Depth { get; }
    public bool IsCloud { get; }
    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    /// <summary>"OneDrive – Uni › .Studium › Semester 5": the path as shown in the pill.</summary>
    public string Display
    {
        get
        {
            if (Depth == 0) return Name;
            string rel = System.IO.Path.GetRelativePath(_rootPath, Path);
            var parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return _rootName + " › " + string.Join(" › ", parts);
        }
    }

    [ObservableProperty] private bool isExpanded;

    /// <summary>"7 folders · 54 PDFs", shown on the selected row.</summary>
    [ObservableProperty] private string countText = "";

    /// <summary>Unknown until the folder is opened, so the chevron is shown optimistically.</summary>
    [ObservableProperty] private bool hasChildren = true;
}

/// <summary>The result of the dialog.</summary>
public readonly record struct FolderChoice(string Path, bool IncludeOffice);

public sealed partial class ChooseFolderViewModel : ObservableObject
{
    private CancellationTokenSource? _countCts;

    public ChooseFolderViewModel(string? current, bool includeOffice)
    {
        this.includeOffice = includeOffice;

        var oneDrives = LibraryScanner.FindOneDriveRoots();
        foreach (var od in oneDrives)
            Rows.Add(MakeRoot(od, isCloud: true));

        // "Documents (this PC)" — skipped when Windows already keeps Documents inside OneDrive.
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(docs) && !oneDrives.Any(o => LibraryScanner.IsSameOrChild(o, docs)))
            Rows.Add(new FolderNode("Documents (this PC)", docs, 0, false, "Documents (this PC)", docs));

        _ = InitAsync(current);
    }

    public ObservableCollection<FolderNode> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse))]
    [NotifyPropertyChangedFor(nameof(PathText))]
    private FolderNode? selected;

    [ObservableProperty] private string statusText = "Pick a folder to see what Graphite will show.";
    [ObservableProperty] private bool statusOk;
    [ObservableProperty] private bool includeOffice;

    public bool CanUse => Selected != null;
    public string PathText => Selected?.Display ?? "No folder selected yet";

    private static FolderNode MakeRoot(string path, bool isCloud)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0) name = path;
        return new FolderNode(name, path, 0, isCloud, name, path);
    }

    private async Task InitAsync(string? current)
    {
        try
        {
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                await ExpandToAsync(current);
            else if (Rows.Count > 0)
                await ToggleAsync(Rows[0]);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Folder tree init failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------- tree

    public async Task ToggleAsync(FolderNode node)
    {
        if (node.IsExpanded) { Collapse(node); return; }

        var children = await Task.Run(() => LibraryScanner.ListSubfolders(node.Path));
        node.HasChildren = children.Count > 0;
        if (children.Count == 0) return;

        int at = Rows.IndexOf(node);
        if (at < 0 || node.IsExpanded) return;
        node.IsExpanded = true;
        string rootName = node.Depth == 0 ? node.Name : RootNameOf(node);
        string rootPath = node.Depth == 0 ? node.Path : RootPathOf(node);
        foreach (var (name, path) in children)
            Rows.Insert(++at, new FolderNode(name, path, node.Depth + 1, false, rootName, rootPath));
    }

    private void Collapse(FolderNode node)
    {
        int at = Rows.IndexOf(node);
        if (at < 0) return;
        bool lostSelection = false;
        while (at + 1 < Rows.Count && Rows[at + 1].Depth > node.Depth)
        {
            if (ReferenceEquals(Rows[at + 1], Selected)) lostSelection = true;
            Rows.RemoveAt(at + 1);
        }
        node.IsExpanded = false;
        if (lostSelection) Selected = node;
    }

    // A child row keeps its root's name/path for the breadcrumb; walk up to the depth-0 row.
    private FolderNode RootOf(FolderNode node)
    {
        int at = Rows.IndexOf(node);
        while (at > 0 && Rows[at].Depth > 0) at--;
        return Rows[Math.Max(at, 0)];
    }
    private string RootNameOf(FolderNode node) => RootOf(node).Name;
    private string RootPathOf(FolderNode node) => RootOf(node).Path;

    /// <summary>Open the tree down to <paramref name="path"/> and select it.</summary>
    private async Task ExpandToAsync(string path)
    {
        var root = Rows.FirstOrDefault(r => r.Depth == 0 && LibraryScanner.IsSameOrChild(r.Path, path));
        if (root == null)
        {
            AddCustomRoot(path);
            return;
        }
        if (!root.IsExpanded) await ToggleAsync(root);

        var current = root;
        string rel = System.IO.Path.GetRelativePath(root.Path, path);
        if (rel != ".")
        {
            foreach (var part in rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string target = System.IO.Path.Combine(current.Path, part);
                var child = Rows.FirstOrDefault(r => r.Depth == current.Depth + 1 && LibraryScanner.SamePath(r.Path, target));
                if (child == null) break;
                current = child;
                // Open every level on the way, except the folder being selected itself.
                if (!LibraryScanner.SamePath(current.Path, path) && !current.IsExpanded) await ToggleAsync(current);
            }
        }
        Selected = current;
    }

    /// <summary>A folder picked with "Browse this PC…": added as its own top-level row and selected.</summary>
    public void AddCustomRoot(string path)
    {
        var existing = Rows.FirstOrDefault(r => LibraryScanner.SamePath(r.Path, path));
        if (existing != null) { Selected = existing; return; }
        var node = MakeRoot(path, isCloud: false);
        Rows.Insert(0, node);
        Selected = node;
    }

    // ------------------------------------------------------------- counting

    partial void OnSelectedChanged(FolderNode? value) => _ = CountAsync(value);

    partial void OnIncludeOfficeChanged(bool value) => _ = CountAsync(Selected);

    private async Task CountAsync(FolderNode? node)
    {
        _countCts?.Cancel();
        if (node == null)
        {
            StatusOk = false;
            StatusText = "Pick a folder to see what Graphite will show.";
            return;
        }

        var cts = _countCts = new CancellationTokenSource();
        StatusOk = false;
        StatusText = "Counting…";
        bool office = IncludeOffice;
        try
        {
            var (subs, files) = await Task.Run(() => LibraryScanner.CountTotals(node.Path, office, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            string noun = office ? "file" : "PDF";
            node.CountText = $"{LibraryScanner.Plural(subs, "folder")} · {LibraryScanner.Plural(files, noun)}";
            StatusOk = true;
            StatusText = $"Found {LibraryScanner.Plural(subs, "subfolder")} and {LibraryScanner.Plural(files, noun)}";
        }
        catch (OperationCanceledException) { /* a newer selection took over */ }
        catch (Exception ex)
        {
            StatusOk = false;
            StatusText = "Can't read this folder.";
            System.Diagnostics.Debug.WriteLine($"Count failed: {ex.Message}");
        }
    }
}

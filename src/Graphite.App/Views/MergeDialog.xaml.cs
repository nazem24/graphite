using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.App.Interop;
using Graphite.App.Services;
using Graphite.Core.Rendering;
using Microsoft.Win32;

namespace Graphite.App.Views;

/// <summary>One input to a merge: a file on disk, or the bytes of an open document.</summary>
public sealed record MergeSource(string? Path, byte[]? Bytes);

public sealed partial class MergeItem : ObservableObject
{
    public MergeItem(MergeSource source, string name, string where, int pageCount, double thumbW, double thumbH, ImageSource? thumb)
    {
        Source = source;
        Name = name;
        Info = $"{pageCount} {(pageCount == 1 ? "page" : "pages")}  \u00B7  {where}";
        PageCount = pageCount;
        ThumbW = thumbW;
        ThumbH = thumbH;
        Thumb = thumb;
    }

    public MergeSource Source { get; }
    public string Name { get; }
    public string Info { get; }
    public int PageCount { get; }
    public double ThumbW { get; }
    public double ThumbH { get; }
    public ImageSource? Thumb { get; }

    [ObservableProperty] private int _number;
}

/// <summary>Merge dialog: a reorderable list of PDFs with a first-page preview each.</summary>
public partial class MergeDialog : Window
{
    private readonly ObservableCollection<MergeItem> _items = new();
    private List<MergeSource>? _result;
    private int _loading;

    public MergeDialog()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;
        Loaded += (_, _) => Backdrop.Apply(this, ThemeService.IsDark);
        UpdateState(null);
    }

    /// <summary>Shows the dialog and returns the chosen inputs in order, or null if cancelled.
    /// <paramref name="initial"/> pre-fills the list (e.g. from a file picker). When
    /// <paramref name="currentBytes"/> is given, the open document is put first in the list.</summary>
    public static List<MergeSource>? Show(Window owner, IEnumerable<string>? initial = null,
        string? currentName = null, byte[]? currentBytes = null)
    {
        var dlg = new MergeDialog { Owner = owner };
        if (currentBytes != null)
            dlg.PromptText.Text = "Your open document is first in the list. Add the PDFs to join with it and put them in the order you want. You can also drop PDFs anywhere in this window.";
        _ = dlg.SeedAsync(initial, currentName, currentBytes);
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private async Task SeedAsync(IEnumerable<string>? initial, string? currentName, byte[]? currentBytes)
    {
        if (currentBytes != null) await AddCurrentAsync(currentName ?? "Open document", currentBytes);
        if (initial != null) await AddFilesAsync(initial);
    }

    private static (int PageCount, double Tw, double Th, byte[] Png) Probe(byte[] pdf)
    {
        var renderer = new PdfRenderer(pdf);
        if (renderer.PageCount == 0) throw new InvalidDataException("No pages.");
        var (w, h) = renderer.PageSizes[0];
        double s = Math.Min(42.0 / Math.Max(1, w), 56.0 / Math.Max(1, h));
        byte[] png = renderer.RenderEncoded(0, s * 2.5);
        return (renderer.PageCount, Math.Max(16, w * s), Math.Max(16, h * s), png);
    }

    private static BitmapImage Decode(byte[] png)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    private async Task AddCurrentAsync(string name, byte[] bytes)
    {
        _loading++;
        UpdateState(null);
        string? problem = null;
        try
        {
            var data = await Task.Run(() => Probe(bytes));
            _items.Add(new MergeItem(new MergeSource(null, bytes), name, "open document",
                data.PageCount, data.Tw, data.Th, Decode(data.Png)));
            Renumber();
        }
        catch { problem = "Couldn\u2019t read the open document."; }
        finally { _loading--; }
        UpdateState(problem);
    }

    // ------------------------------------------------------------- adding

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var skipped = new List<string>();
        _loading++;
        UpdateState(null);
        try
        {
            foreach (string path in paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var data = await Task.Run(() => Probe(File.ReadAllBytes(path)));
                    _items.Add(new MergeItem(new MergeSource(path, null), Path.GetFileName(path),
                        Path.GetDirectoryName(path) ?? "", data.PageCount, data.Tw, data.Th, Decode(data.Png)));
                    Renumber();
                }
                catch { skipped.Add(Path.GetFileName(path)); }
            }
        }
        finally { _loading--; }
        UpdateState(skipped.Count == 0 ? null
            : $"Couldn’t read: {string.Join(", ", skipped)} (damaged or password-protected).");
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "PDF documents|*.pdf", Title = "Add PDFs to merge" };
        if (dlg.ShowDialog(this) == true) await AddFilesAsync(dlg.FileNames);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await AddFilesAsync(files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------- ordering

    private static MergeItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as MergeItem;

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } it) return;
        int i = _items.IndexOf(it);
        if (i > 0) _items.Move(i, i - 1);
        Renumber();
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } it) return;
        int i = _items.IndexOf(it);
        if (i >= 0 && i < _items.Count - 1) _items.Move(i, i + 1);
        Renumber();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } it) return;
        _items.Remove(it);
        Renumber();
        UpdateState(null);
    }

    private void Renumber()
    {
        for (int i = 0; i < _items.Count; i++) _items[i].Number = i + 1;
        UpdateState(null);
    }

    private void UpdateState(string? message)
    {
        int n = _items.Count;
        int pages = _items.Sum(i => i.PageCount);
        EmptyHint.Visibility = n == 0 && _loading == 0 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = n < 2 ? "Merge" : $"Merge {n} files";
        OkButton.IsEnabled = n >= 2 && _loading == 0;
        StatusText.Text = message
            ?? (_loading > 0 ? "Reading files…"
            : n == 0 ? ""
            : n == 1 ? "Add at least one more PDF to merge."
            : $"{n} files · {pages} pages in total");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _result = _items.Select(i => i.Source).ToList();
        DialogResult = true;
    }
}

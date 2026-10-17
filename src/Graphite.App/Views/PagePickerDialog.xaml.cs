using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.App.Interop;
using Graphite.App.Services;
using Graphite.Core.Pdf;
using Graphite.Core.Rendering;

namespace Graphite.App.Views;

public enum PagePickMode { Delete, Extract, Insert }

/// <summary>What the picker returns: 0-based page indices, and (insert mode) where to put them.</summary>
public sealed record PagePickResult(IReadOnlyList<int> Pages, int InsertAt);

/// <summary>One thumbnail tile in the picker.</summary>
public sealed partial class PageTile : ObservableObject
{
    public PageTile(int index, double pageW, double pageH)
    {
        Index = index;
        double s = Math.Min(108.0 / pageW, 148.0 / pageH);
        ThumbW = Math.Max(24, pageW * s);
        ThumbH = Math.Max(24, pageH * s);
        RenderScale = 2.0 * s;
    }

    public int Index { get; }
    public int Number => Index + 1;
    public double ThumbW { get; }
    public double ThumbH { get; }
    public double RenderScale { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _thumb;
}

/// <summary>Visual page picker used by Delete, Extract and Insert pages.</summary>
public partial class PagePickerDialog : Window
{
    private readonly PagePickMode _mode;
    private readonly List<PageTile> _tiles = new();
    private readonly int _targetCurrent;
    private readonly int _targetCount;
    private readonly CancellationTokenSource _cts = new();
    private bool _sync;
    private bool _invalid;
    private PagePickResult? _result;

    private PagePickerDialog(PagePickMode mode, PdfRenderer renderer, string title, string prompt,
        IEnumerable<int> preselect, int targetCurrent, int targetCount)
    {
        InitializeComponent();
        _mode = mode;
        _targetCurrent = targetCurrent;
        _targetCount = targetCount;
        Title = title;
        PromptText.Text = prompt;

        Resources["Pick.Brush"] = mode == PagePickMode.Delete
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D))
            : (Brush)FindResource("App.AccentSystemBrush");

        for (int i = 0; i < renderer.PageCount; i++)
        {
            var (w, h) = renderer.PageSizes[i];
            var tile = new PageTile(i, Math.Max(1, w), Math.Max(1, h));
            tile.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PageTile.IsSelected)) OnTilesChanged();
            };
            _tiles.Add(tile);
        }
        foreach (int i in preselect)
            if (i >= 0 && i < _tiles.Count) _tiles[i].IsSelected = true;
        Tiles.ItemsSource = _tiles;

        if (mode == PagePickMode.Insert)
        {
            PlacementPanel.Visibility = Visibility.Visible;
            AfterRadio.Content = new TextBlock { Text = $"After current page ({targetCurrent + 1})" };
            BeforeRadio.Content = new TextBlock { Text = $"Before current page ({targetCurrent + 1})" };
        }

        _sync = true;
        RangeBox.Text = ToRanges(_tiles.Where(t => t.IsSelected).Select(t => t.Index));
        _sync = false;
        UpdateState();

        Loaded += (_, _) =>
        {
            Backdrop.Apply(this, ThemeService.IsDark);
            RangeBox.Focus();
            RangeBox.CaretIndex = RangeBox.Text.Length;
            _ = LoadThumbsAsync(renderer);
        };
        Closed += (_, _) => _cts.Cancel();
    }

    // ------------------------------------------------------------- thumbnails

    private async Task LoadThumbsAsync(PdfRenderer renderer)
    {
        var ct = _cts.Token;
        foreach (var tile in _tiles)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                byte[] png = await Task.Run(() => renderer.RenderEncoded(tile.Index, tile.RenderScale), ct);
                if (ct.IsCancellationRequested) return;
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(png))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                tile.Thumb = bmp;
            }
            catch (OperationCanceledException) { return; }
            catch { /* a page that won't render keeps its grey placeholder */ }
        }
    }

    // ------------------------------------------------------------- selection

    private void OnTilesChanged()
    {
        if (_sync) return;
        _sync = true;
        RangeBox.Text = ToRanges(_tiles.Where(t => t.IsSelected).Select(t => t.Index));
        _sync = false;
        _invalid = false;
        UpdateState();
    }

    private void SetSelection(Func<int, bool> pick)
    {
        _sync = true;
        foreach (var t in _tiles) t.IsSelected = pick(t.Index);
        RangeBox.Text = ToRanges(_tiles.Where(t => t.IsSelected).Select(t => t.Index));
        _sync = false;
        _invalid = false;
        UpdateState();
    }

    private void All_Click(object sender, RoutedEventArgs e) => SetSelection(_ => true);
    private void None_Click(object sender, RoutedEventArgs e) => SetSelection(_ => false);
    private void Odd_Click(object sender, RoutedEventArgs e) => SetSelection(i => i % 2 == 0);   // pages 1, 3, 5…
    private void Even_Click(object sender, RoutedEventArgs e) => SetSelection(i => i % 2 == 1);  // pages 2, 4, 6…

    private void RangeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        string text = RangeBox.Text.Trim();
        HashSet<int> pages;
        if (text.Length == 0) pages = new();
        else
        {
            try { pages = PageOperations.ParsePageRanges(text, _tiles.Count).ToHashSet(); }
            catch
            {
                _invalid = true;
                UpdateState();
                return;
            }
        }
        _invalid = false;
        _sync = true;
        foreach (var t in _tiles) t.IsSelected = pages.Contains(t.Index);
        _sync = false;
        UpdateState();
    }

    /// <summary>[0,1,2,4,6,7] -> "1-3, 5, 7-8".</summary>
    private static string ToRanges(IEnumerable<int> zeroBased)
    {
        var list = zeroBased.OrderBy(i => i).ToList();
        var parts = new List<string>();
        for (int i = 0; i < list.Count;)
        {
            int j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
            parts.Add(j == i ? $"{list[i] + 1}" : $"{list[i] + 1}-{list[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private void UpdateState()
    {
        int n = _tiles.Count(t => t.IsSelected);
        int total = _tiles.Count;
        string pages = n == 1 ? "page" : "pages";
        bool all = _mode == PagePickMode.Delete && n >= total;

        OkButton.Content = _mode switch
        {
            PagePickMode.Delete => n == 0 ? "Delete" : $"Delete {n} {pages}",
            PagePickMode.Extract => n == 0 ? "Extract…" : $"Extract {n} {pages}…",
            _ => n == 0 ? "Insert" : $"Insert {n} {pages}",
        };
        OkButton.IsEnabled = n > 0 && !all && !_invalid;
        StatusText.Text = _invalid ? "That isn't a valid page range, try something like 1, 3-5."
            : all ? "At least one page has to stay in the document."
            : $"{n} of {total} {(total == 1 ? "page" : "pages")} selected";
    }

    // ------------------------------------------------------------- result

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var pages = _tiles.Where(t => t.IsSelected).Select(t => t.Index).ToList();
        int at = 0;
        if (_mode == PagePickMode.Insert)
        {
            at = StartRadio.IsChecked == true ? 0
               : EndRadio.IsChecked == true ? _targetCount
               : BeforeRadio.IsChecked == true ? _targetCurrent
               : _targetCurrent + 1;
        }
        _result = new PagePickResult(pages, at);
        DialogResult = true;
    }

    /// <summary>Delete / Extract: pick pages of <paramref name="renderer"/>'s document.</summary>
    public static PagePickResult? Pick(Window owner, PagePickMode mode, PdfRenderer renderer, int currentPageIndex)
    {
        bool del = mode == PagePickMode.Delete;
        var dlg = new PagePickerDialog(mode, renderer,
            del ? "Delete pages" : "Extract pages",
            del ? "Click the pages you want to remove."
                : "Click the pages to copy into a new PDF. Your document stays as it is.",
            new[] { currentPageIndex }, 0, 0) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    /// <summary>Insert: pick which pages of the other PDF to bring in, and where they go.</summary>
    public static PagePickResult? PickForInsert(Window owner, PdfRenderer source, string sourceName,
        int targetCurrentIndex, int targetPageCount)
    {
        var dlg = new PagePickerDialog(PagePickMode.Insert, source, "Insert pages",
            $"Choose which pages of “{sourceName}” to insert. All are selected to start with.",
            Enumerable.Range(0, source.PageCount), targetCurrentIndex, targetPageCount) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }
}

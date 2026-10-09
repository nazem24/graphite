using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Graphite.App.Services;
using Graphite.App.ViewModels;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Graphite.App.Views;

/// <summary>
/// The document editor: a white sheet (a RichTextBox over a FlowDocument), the formatting bar
/// above it, an outline beside it, and the page / word / zoom pill below. Content and file
/// handling live in <see cref="WriterViewModel"/> and <see cref="DocxCodec"/>; this file does
/// what only the control can — applying formatting to the selection, inserting pictures, links
/// and tables, and keeping the bar's buttons in step with the caret.
/// </summary>
public partial class WriterView : UserControl, IWriterSurface
{
    private static readonly string[] SizeList =
        { "8", "9", "10", "10.5", "11", "12", "14", "16", "18", "20", "24", "28", "32", "36", "48", "72" };

    private static readonly string[] FontCandidates =
    {
        "Segoe UI", "Segoe UI Semibold", "Calibri", "Arial", "Times New Roman", "Georgia", "Cambria", "Verdana",
        "Tahoma", "Trebuchet MS", "Palatino Linotype", "Garamond", "Consolas", "Courier New", "Comic Sans MS",
    };

    private static readonly string[] TextPalette =
    {
        "#000000", "#434343", "#7F7F7F", "#BFBFBF", "#FFFFFF", "#1F3A5F", "#2980B9",
        "#16A085", "#27AE60", "#F1C40F", "#E67E22", "#C0392B", "#D81B60", "#8E44AD",
    };

    private static readonly string[] MarkPalette =
    {
        "#FFF176", "#C5E1A5", "#80DEEA", "#F8BBD0", "#FFCC80", "#D1C4E9", "#E0E0E0",
    };

    private readonly DispatcherTimer _statsTimer;
    private WriterViewModel? _vm;
    private bool _updating;
    private int _guideCount = -1;

    private bool _introPlayed;

    public WriterView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_introPlayed) return;
            _introPlayed = true;
            Motion.FadeIn(this, 240, 0, 8);
        };

        StyleBox.ItemsSource = DocStyles.All.Select(DocStyles.DisplayName).ToList();
        var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
        FontBox.ItemsSource = FontCandidates.Where(installed.Contains).ToList();
        SizeBox.ItemsSource = SizeList;
        TextSwatches.ItemsSource = TextPalette.Select(Swatch).ToList();
        MarkSwatches.ItemsSource = MarkPalette.Select(Swatch).ToList();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _statsTimer.Tick += (_, _) =>
        {
            _statsTimer.Stop();
            _vm?.RefreshStats();
            UpdatePageInfo();
        };

        Editor.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        DataObject.AddPastingHandler(Editor, Editor_Pasting);
        Editor.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(Editor_RequestNavigate));

        DataContextChanged += WriterView_DataContextChanged;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusEditor);
        };
        Unloaded += (_, _) => _statsTimer.Stop();
    }

    private static SolidColorBrush Swatch(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------- wiring to the view-model

    private void WriterView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is WriterViewModel old)
        {
            old.PageChanged -= ApplyPage;
            old.PropertyChanged -= Vm_PropertyChanged;
            if (ReferenceEquals(old.Surface, this)) old.Surface = null;
        }

        _vm = e.NewValue as WriterViewModel;
        if (_vm == null) return;

        _vm.Document.Language = Editor.Language;
        Editor.Document = _vm.Document;
        _vm.Surface = this;
        _vm.PageChanged += ApplyPage;
        _vm.PropertyChanged += Vm_PropertyChanged;
        ApplyPage();
        ApplyZoom();

        // Edits made while the document was being loaded are not the person's: only start
        // tracking once the editor has settled.
        var vm = _vm;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!ReferenceEquals(vm, _vm)) return;
            vm.BeginTracking();
            UpdateFormatState();
            UpdatePageInfo();
        });
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WriterViewModel.Zoom)) ApplyZoom();
    }

    /// <summary>Paper size or margins changed (or the document was just attached).</summary>
    private void ApplyPage()
    {
        if (_vm == null) return;
        var page = _vm.Page;
        Editor.Width = page.WidthDip;
        Editor.MinHeight = page.HeightDip;
        // One column as wide as the sheet, so text never splits into newspaper columns.
        _vm.Document.PageWidth = page.WidthDip;
        _vm.Document.ColumnWidth = page.WidthDip;
        _vm.Document.PagePadding = new Thickness(page.MarginDip);
        _guideCount = -1;
        PageGuides.Width = page.WidthDip;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdatePageInfo);
    }

    private void ApplyZoom()
    {
        double z = _vm?.Zoom ?? 1.0;
        Sheet.LayoutTransform = new ScaleTransform(z, z);
    }

    public void FocusEditor()
    {
        Editor.Focus();
        Keyboard.Focus(Editor);
    }

    /// <summary>Something changed in the document by code: autosave it and refresh the counts.</summary>
    private void Edited()
    {
        _vm?.MarkChanged();
        _statsTimer.Stop();
        _statsTimer.Start();
    }

    // ------------------------------------------------------------- editor events

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => Edited();

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateFormatState();
        _statsTimer.Stop();
        _statsTimer.Start();
    }

    /// <summary>Enter at the end of a heading starts ordinary text; Enter in the middle of one
    /// keeps both halves as headings (the framework does not always carry the style across).</summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (!Editor.Selection.IsEmpty) return;
        var para = Editor.CaretPosition.Paragraph;
        if (para == null || para.Parent is ListItem) return;
        string id = DocStyles.Of(para);
        if (id == DocStyles.Normal) return;

        bool atEnd = string.IsNullOrEmpty(new TextRange(Editor.CaretPosition, para.ContentEnd).Text);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => AfterEnterInHeading(id, atEnd));
    }

    private void AfterEnterInHeading(string id, bool atEnd)
    {
        var current = Editor.CaretPosition.Paragraph;
        if (current?.PreviousBlock is not Paragraph previous) return;
        Editor.BeginChange();
        try
        {
            if (DocStyles.Of(previous) != id) DocStyles.Apply(previous, id);
            if (atEnd)
            {
                DocStyles.Apply(current, DocStyles.Normal);
                ClearInlineSizing(current.Inlines, clearWeight: true);
            }
            else if (DocStyles.Of(current) != id)
            {
                DocStyles.Apply(current, id);
            }
        }
        finally { Editor.EndChange(); }
        Edited();
    }

    private void Editor_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        var uri = e.Uri;
        if (uri == null || !(uri.Scheme is "http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { App.LogError("Opening a link failed", ex); }
    }

    /// <summary>A picture on the clipboard (a screenshot, say) becomes an image in the document.</summary>
    private void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;
        if (!data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Text) ||
            data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Rtf) ||
            data.GetDataPresent(DataFormats.Xaml) || data.GetDataPresent(DataFormats.XamlPackage))
            return;

        if (data.GetData(DataFormats.Bitmap) is not BitmapSource bitmap) return;
        e.CancelCommand();
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            InsertPicture(ms.ToArray());
        }
        catch (Exception ex) { App.LogError("Pasting a picture failed", ex); }
    }

    private void Surface_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm == null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _vm.Zoom = Math.Round(_vm.Zoom + (e.Delta > 0 ? 0.1 : -0.1), 2);
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (_vm != null) _vm.Zoom = Math.Round(_vm.Zoom + 0.1, 2); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_vm != null) _vm.Zoom = Math.Round(_vm.Zoom - 0.1, 2); }
    private void ZoomReset_Click(object sender, RoutedEventArgs e) { if (_vm != null) _vm.Zoom = 1.0; }

    // ------------------------------------------------------------- the bar follows the caret

    private static bool IsBullet(TextMarkerStyle style) =>
        style is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None;

    private void UpdateFormatState()
    {
        if (_vm == null) return;
        _updating = true;
        try
        {
            var sel = Editor.Selection;

            BoldBtn.IsChecked = sel.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600;
            ItalicBtn.IsChecked = sel.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle fs && fs == FontStyles.Italic;

            var decorations = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            UnderlineBtn.IsChecked = decorations != null && decorations.Any(d => d.Location == TextDecorationLocation.Underline);
            StrikeBtn.IsChecked = decorations != null && decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);

            string? family = (sel.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily)?.Source;
            FontBox.SelectedItem = family != null && FontBox.Items.Contains(family) ? family : null;

            if (sel.GetPropertyValue(TextElement.FontSizeProperty) is double dip)
            {
                string pt = (Math.Round(Units.DipToPt(dip) * 2) / 2).ToString("0.#", CultureInfo.InvariantCulture);
                SizeBox.SelectedItem = SizeList.Contains(pt) ? pt : null;
            }
            else SizeBox.SelectedItem = null;

            var para = sel.Start.Paragraph;
            StyleBox.SelectedIndex = para == null ? -1 : Array.IndexOf(DocStyles.All, DocStyles.Of(para));

            var align = para?.TextAlignment ?? TextAlignment.Left;
            AlignLeftBtn.IsChecked = align == TextAlignment.Left;
            AlignCenterBtn.IsChecked = align == TextAlignment.Center;
            AlignRightBtn.IsChecked = align == TextAlignment.Right;
            AlignJustifyBtn.IsChecked = align == TextAlignment.Justify;

            var list = (para?.Parent as ListItem)?.Parent as List;
            BulletBtn.IsChecked = list != null && IsBullet(list.MarkerStyle);
            NumberBtn.IsChecked = list != null && !IsBullet(list.MarkerStyle);

            _vm.InTable = Ancestor<TableCell>(sel.Start.Parent) != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Format state update failed: {ex.Message}");
        }
        finally { _updating = false; }
    }

    /// <summary>The toggles driven by editing commands: refresh the bar and autosave once the command ran.</summary>
    private void Format_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            UpdateFormatState();
            Edited();
        });

    private static T? Ancestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var d = start; d != null; d = LogicalTreeHelper.GetParent(d))
            if (d is T found) return found;
        return null;
    }

    // ------------------------------------------------------------- page indicator

    private void UpdatePageInfo()
    {
        if (_vm == null) return;
        try
        {
            var page = _vm.Page;
            double margin = page.MarginDip;
            double perPage = Math.Max(50, page.HeightDip - 2 * margin);
            double content = Math.Max(0, Editor.ActualHeight - 2 * margin);
            int total = Math.Max(1, (int)Math.Ceiling(content / perPage - 0.001));

            int current = 1;
            var rect = Editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty) current = Math.Clamp((int)Math.Floor((rect.Top - margin) / perPage) + 1, 1, total);

            PageInfo.Text = $"Page {current} of {total}";
            if (total != _guideCount)
            {
                _guideCount = total;
                PageGuides.Children.Clear();
                for (int k = 1; k < total; k++)
                {
                    var line = new Rectangle
                    {
                        Width = page.WidthDip,
                        Height = 1,
                        Fill = Brushes.Gray,
                        Opacity = 0.35,
                    };
                    Canvas.SetTop(line, margin + k * perPage);
                    PageGuides.Children.Add(line);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Page info failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- applying formatting

    private List<Paragraph> SelectedParagraphs()
    {
        var found = new List<Paragraph>();
        var seen = new HashSet<Paragraph>();
        var sel = Editor.Selection;
        var end = sel.End;
        for (TextPointer? tp = sel.Start; tp != null && tp.CompareTo(end) <= 0; tp = tp.GetNextContextPosition(LogicalDirection.Forward))
            if (tp.Paragraph is { } p && seen.Add(p)) found.Add(p);
        if (end.Paragraph is { } last && seen.Add(last)) found.Add(last);
        return found;
    }

    /// <summary>A paragraph style should win over sizes left on individual runs.</summary>
    private static void ClearInlineSizing(InlineCollection inlines, bool clearWeight)
    {
        foreach (Inline inline in inlines)
        {
            inline.ClearValue(TextElement.FontSizeProperty);
            if (clearWeight) inline.ClearValue(TextElement.FontWeightProperty);
            if (inline is Span span) ClearInlineSizing(span.Inlines, clearWeight);
        }
    }

    private void StyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _vm == null || StyleBox.SelectedIndex < 0) return;
        string id = DocStyles.All[StyleBox.SelectedIndex];
        Editor.BeginChange();
        try
        {
            foreach (var p in SelectedParagraphs())
            {
                DocStyles.Apply(p, id);
                ClearInlineSizing(p.Inlines, clearWeight: id != DocStyles.Normal);
            }
        }
        finally { Editor.EndChange(); }
        Edited();
        FocusEditor();
    }

    private void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || FontBox.SelectedItem is not string name) return;
        Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(name));
        Edited();
        FocusEditor();
    }

    private void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SizeBox.SelectedItem is not string text ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pt)) return;
        Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, Units.PtToDip(pt));
        Edited();
        FocusEditor();
    }

    private void Underline_Click(object sender, RoutedEventArgs e) =>
        ToggleDecoration(TextDecorationLocation.Underline);

    private void Strike_Click(object sender, RoutedEventArgs e) =>
        ToggleDecoration(TextDecorationLocation.Strikethrough);

    /// <summary>Underline and strikethrough share one property, so toggling one keeps the other.</summary>
    private void ToggleDecoration(TextDecorationLocation location)
    {
        var sel = Editor.Selection;
        var current = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool has = current != null && current.Any(d => d.Location == location);

        var next = new TextDecorationCollection();
        if (current != null)
            foreach (var d in current)
                if (d.Location != location) next.Add(d);
        if (!has)
            foreach (var d in location == TextDecorationLocation.Underline ? TextDecorations.Underline : TextDecorations.Strikethrough)
                next.Add(d);

        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
        Edited();
        UpdateFormatState();
        FocusEditor();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SolidColorBrush brush } button) return;

        // The swatch lives in one of the two popups: find which, apply, close it.
        bool isText = TextSwatches.IsAncestorOf(button);
        if (isText)
        {
            Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
            TextColorBar.Fill = brush;
            TextColorBtn.IsChecked = false;
        }
        else
        {
            Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, brush);
            MarkColorBar.Fill = brush;
            MarkColorBtn.IsChecked = false;
        }
        Edited();
        FocusEditor();
    }

    private void TextColorAuto_Click(object sender, RoutedEventArgs e)
    {
        Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);
        TextColorBar.Fill = Brushes.Black;
        TextColorBtn.IsChecked = false;
        Edited();
        FocusEditor();
    }

    private void MarkColorNone_Click(object sender, RoutedEventArgs e)
    {
        Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        MarkColorBtn.IsChecked = false;
        Edited();
        FocusEditor();
    }

    private void Spacing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SpacingItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double multiple)) return;

        Editor.BeginChange();
        try
        {
            foreach (var p in SelectedParagraphs())
            {
                if (multiple <= 1.0001) { p.ClearValue(Block.LineHeightProperty); continue; }
                double natural = p.FontSize * (p.FontFamily?.LineSpacing ?? 1.33);
                p.LineHeight = natural * multiple;
            }
        }
        finally { Editor.EndChange(); }
        Edited();
        FocusEditor();
    }

    // ------------------------------------------------------------- outline

    private void OutlineItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OutlineItem item) return;
        Editor.CaretPosition = item.Paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        FocusEditor();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var rect = Editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return;
            double y = Editor.TranslatePoint(rect.TopLeft, Surface).Y;
            Surface.ScrollToVerticalOffset(Math.Max(0, Surface.VerticalOffset + y - 48));
        });
        e.Handled = true;
    }

    // ------------------------------------------------------------- IWriterSurface

    public void Undo()
    {
        if (Editor.CanUndo) Editor.Undo();
        FocusEditor();
    }

    public void Redo()
    {
        if (Editor.CanRedo) Editor.Redo();
        FocusEditor();
    }

    public void InsertImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Insert picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { InsertPicture(File.ReadAllBytes(dialog.FileName)); }
        catch (Exception ex)
        {
            App.LogError("Inserting a picture failed", ex);
            MessageDialog.Show(Window.GetWindow(this), $"Couldn't insert that picture.\n{ex.Message}", "Graphite",
                DialogButtons.OK, DialogIcon.Warning);
        }
    }

    private void InsertPicture(byte[] bytes)
    {
        if (_vm == null) return;
        string extension = DocxCodec.ImageExtension(bytes);
        var bitmap = DocxCodec.LoadBitmap(bytes);

        if (extension.Length == 0)
        {
            // A format Word can't hold (TIFF…): store it as PNG.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            bytes = ms.ToArray();
            extension = "png";
            bitmap = DocxCodec.LoadBitmap(bytes);
        }

        double width = bitmap.Width, height = bitmap.Height;
        double max = _vm.Page.TextWidthDip;
        if (width > max && max > 0)
        {
            height *= max / width;
            width = max;
        }

        var image = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            Tag = new EmbeddedImage(bytes, extension),
        };

        Editor.BeginChange();
        try
        {
            if (!Editor.Selection.IsEmpty) Editor.Selection.Text = "";
            _ = new InlineUIContainer(image, Editor.CaretPosition);
        }
        finally { Editor.EndChange(); }
        Edited();
        FocusEditor();
    }

    public void InsertLink()
    {
        var owner = Window.GetWindow(this);
        if (owner == null) return;

        Hyperlink? existing = Ancestor<Hyperlink>(Editor.Selection.Start.Parent);
        string initial = existing?.NavigateUri?.OriginalString ?? "https://";
        string? entered = InputDialog.Show(owner, "Link", "Address to link to", initial)?.Trim();
        if (string.IsNullOrEmpty(entered)) return;

        if (!entered.Contains("://") && !entered.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            entered = entered.Contains('@') && !entered.Contains(' ') ? "mailto:" + entered : "https://" + entered;
        if (!Uri.TryCreate(entered, UriKind.Absolute, out var uri) || !(uri.Scheme is "http" or "https" or "mailto"))
        {
            MessageDialog.Show(owner, "That doesn't look like a web or email address.", "Graphite",
                DialogButtons.OK, DialogIcon.Info);
            return;
        }

        if (existing != null)
        {
            existing.NavigateUri = uri;
            existing.ToolTip = uri.OriginalString;
            Edited();
            FocusEditor();
            return;
        }

        Editor.BeginChange();
        try
        {
            Hyperlink link;
            if (Editor.Selection.IsEmpty)
            {
                link = new Hyperlink(new Run(entered.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? entered[7..] : entered),
                    Editor.CaretPosition);
            }
            else
            {
                link = new Hyperlink(Editor.Selection.Start, Editor.Selection.End);
            }
            link.NavigateUri = uri;
            link.ToolTip = uri.OriginalString;
            Editor.CaretPosition = link.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
        }
        catch (InvalidOperationException)
        {
            MessageDialog.Show(owner, "Select text within a single paragraph to turn it into a link.", "Graphite",
                DialogButtons.OK, DialogIcon.Info);
        }
        finally { Editor.EndChange(); }
        Edited();
        FocusEditor();
    }

    // ---- tables

    /// <summary>The outermost block holding <paramref name="element"/> that sits directly in the document.</summary>
    private static Block? TopBlockOf(TextElement? element)
    {
        Block? top = null;
        DependencyObject? d = element;
        while (d is TextElement te)
        {
            if (te is Block b) top = b;
            d = te.Parent;
            if (d is FlowDocument) break;
        }
        return top;
    }

    public void InsertTable(int rows, int columns)
    {
        if (_vm == null) return;
        var table = DocxCodec.NewTable(rows, columns);
        var document = _vm.Document;
        var top = TopBlockOf(Editor.CaretPosition.Paragraph);

        Editor.BeginChange();
        try
        {
            if (top == null) document.Blocks.Add(table);
            else if (top is Paragraph p && string.IsNullOrWhiteSpace(new TextRange(p.ContentStart, p.ContentEnd).Text))
                document.Blocks.InsertBefore(top, table);
            else
                document.Blocks.InsertAfter(top, table);

            // There must be somewhere to type after a table that ends the document.
            if (ReferenceEquals(document.Blocks.LastBlock, table)) document.Blocks.Add(DocStyles.NewParagraph());
        }
        finally { Editor.EndChange(); }

        var firstCell = table.RowGroups.First().Rows.First().Cells.First();
        if (firstCell.Blocks.FirstBlock is Paragraph cellParagraph)
            Editor.CaretPosition = cellParagraph.ContentStart;
        Edited();
        FocusEditor();
    }

    public void TableAction(string action)
    {
        var cell = Ancestor<TableCell>(Editor.CaretPosition.Parent);
        if (cell == null || cell.Parent is not TableRow row || row.Parent is not TableRowGroup group || group.Parent is not Table table)
            return;

        int column = row.Cells.ToList().IndexOf(cell);
        Editor.BeginChange();
        try
        {
            switch (action)
            {
                case "row":
                    group.Rows.Insert(group.Rows.IndexOf(row) + 1, DocxCodec.NewRow(Math.Max(1, row.Cells.Count)));
                    break;

                case "column":
                {
                    table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
                    var headerRow = table.RowGroups.FirstOrDefault()?.Rows.FirstOrDefault();
                    foreach (var g in table.RowGroups.ToList())
                        foreach (var r in g.Rows.ToList())
                        {
                            var added = DocxCodec.NewCell(header: ReferenceEquals(r, headerRow));
                            var cells = r.Cells.ToList();
                            if (column >= 0 && column < cells.Count) r.Cells.Insert(column + 1, added);
                            else r.Cells.Add(added);
                        }
                    break;
                }

                case "deleteRow":
                    if (group.Rows.Count > 1) group.Rows.Remove(row);
                    else RemoveTable(table);
                    break;

                case "deleteTable":
                    RemoveTable(table);
                    break;
            }
        }
        finally { Editor.EndChange(); }

        if (_vm != null && _vm.Document.Blocks.Count == 0) _vm.Document.Blocks.Add(DocStyles.NewParagraph());
        Edited();
        FocusEditor();
    }

    private static void RemoveTable(Table table)
    {
        switch (table.Parent)
        {
            case FlowDocument fd: fd.Blocks.Remove(table); break;
            case TableCell tc: tc.Blocks.Remove(table); break;
            case ListItem li: li.Blocks.Remove(table); break;
            case Section section: section.Blocks.Remove(table); break;
        }
    }
}

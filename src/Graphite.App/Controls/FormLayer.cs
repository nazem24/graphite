using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Graphite.App.ViewModels;
using Graphite.App.Views;
using Graphite.Core;
using Graphite.Core.Forms;

namespace Graphite.App.Controls;

/// <summary>
/// Fill-in overlay for one page of an interactive PDF form. DataContext = PageViewModel.
///
/// The page bitmap PDFium renders shows the field values (Graphite writes each value with an
/// appearance stream and re-renders the page), so the overlay controls are see-through until
/// they're used: a light tint marks where the fields are, a text field turns into an opaque
/// text box while it is being typed in, check boxes and radio buttons toggle on click, and
/// choice fields open a menu of their options. Active only with the Select tool, so markup
/// tools keep working on top of forms.
/// </summary>
public sealed class FormLayer : Panel
{
    private PageViewModel? _page;
    private DocumentViewModel? _doc;
    private readonly Dictionary<UIElement, (FormFieldViewModel Field, FormWidget Widget)> _items = new();

    // Subscriptions to field view-models (which outlive these controls): undone on every rebuild,
    // so recycling a page container doesn't pile up handlers that keep old text boxes alive.
    private readonly List<Action> _unsubscribe = new();

    private static readonly Brush FieldTint = Frozen(Color.FromArgb(0x24, 0x3E, 0x6D, 0xB5));
    private static readonly Brush FieldTintHover = Frozen(Color.FromArgb(0x40, 0x3E, 0x6D, 0xB5));
    private static readonly Brush FieldEditing = Frozen(Colors.White);
    private static readonly Brush FieldOutline = Frozen(Color.FromArgb(0x99, 0x3E, 0x6D, 0xB5));

    // A blank style keeps the app's implicit TextBox style (rounded, padded chrome for dialogs)
    // off these: they must sit exactly on the PDF field.
    private static readonly Style PlainTextBox = new(typeof(TextBox));
    private static readonly Style PlainPasswordBox = new(typeof(PasswordBox));

    public FormLayer()
    {
        DataContextChanged += (_, _) => Attach(DataContext as PageViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as PageViewModel);
    }

    private void Attach(PageViewModel? page)
    {
        if (ReferenceEquals(page, _page) && (page == null || ReferenceEquals(page.Doc, _doc))) return;
        if (_doc != null)
        {
            _doc.PropertyChanged -= Doc_PropertyChanged;
            _doc.FormFieldsReset -= Rebuild;
        }
        _page = page;
        _doc = page?.Doc;
        if (_doc != null)
        {
            _doc.PropertyChanged += Doc_PropertyChanged;
            _doc.FormFieldsReset += Rebuild;
        }
        Rebuild();
    }

    private void Doc_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.Zoom))
        {
            foreach (var child in _items.Keys) ApplyFont(child);
            InvalidateMeasure();
        }
        else if (e.PropertyName == nameof(DocumentViewModel.ActiveTool))
        {
            UpdateVisibility();
        }
    }

    private void UpdateVisibility() =>
        Visibility = _doc is { ActiveTool: ToolKind.Select } && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private double Zoom => _doc?.Zoom ?? 1;

    // ------------------------------------------------------------- building controls

    private void Rebuild()
    {
        // A field being typed in keeps its text: commit it before its control goes away.
        foreach (var child in _items.Keys)
            if (child is TextBox { IsKeyboardFocusWithin: true } tb) Commit(tb);

        foreach (var undo in _unsubscribe) undo();
        _unsubscribe.Clear();
        Children.Clear();
        _items.Clear();
        if (_doc == null || _page == null || !_doc.HasForm) { UpdateVisibility(); return; }

        foreach (var (field, widget) in _doc.FormWidgetsOn(_page.Index))
        {
            UIElement? element = field.Kind switch
            {
                FormFieldKind.Text when field.Model.Password => MakePasswordBox(field),
                FormFieldKind.Text => MakeTextBox(field),
                FormFieldKind.CheckBox or FormFieldKind.Radio => MakeToggle(field, widget),
                FormFieldKind.ComboBox or FormFieldKind.ListBox => MakeChoice(field),
                _ => null,
            };
            if (element == null) continue;
            if (element is FrameworkElement fe) fe.ToolTip = field.Hint;
            _items[element] = (field, widget);
            ApplyFont(element);
            Children.Add(element);
        }
        UpdateVisibility();
        InvalidateMeasure();
    }

    private TextBox MakeTextBox(FormFieldViewModel field)
    {
        var tb = new TextBox
        {
            Style = PlainTextBox,
            Text = field.Value,
            Background = FieldTint,
            Foreground = Brushes.Transparent,   // the rendered page shows the value
            CaretBrush = Brushes.Black,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(1, 0, 1, 0),
            FontFamily = new FontFamily("Arial"),
            AcceptsReturn = field.Model.Multiline,
            TextWrapping = field.Model.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = field.Model.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            MaxLength = field.Model.MaxLength,
            TextAlignment = field.Model.Alignment switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
            Cursor = Cursors.IBeam,
        };
        tb.GotKeyboardFocus += (_, _) => ShowEditing(tb, true);
        tb.LostKeyboardFocus += (_, _) => Commit(tb);
        tb.MouseEnter += (_, _) => { if (!tb.IsKeyboardFocusWithin) tb.Background = FieldTintHover; };
        tb.MouseLeave += (_, _) => { if (!tb.IsKeyboardFocusWithin) tb.Background = FieldTint; };
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                // Throw the typing away and step out of the field.
                tb.Text = field.Value;
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !field.Model.Multiline)
            {
                tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
        };
        // The value changed elsewhere (undo/redo rebuilds us; this covers other paths).
        PropertyChangedEventHandler onValue = (_, e) =>
        {
            if (e.PropertyName == nameof(FormFieldViewModel.Value) && !tb.IsKeyboardFocusWithin)
                tb.Text = field.Value;
        };
        field.PropertyChanged += onValue;
        _unsubscribe.Add(() => field.PropertyChanged -= onValue);
        return tb;
    }

    private PasswordBox MakePasswordBox(FormFieldViewModel field)
    {
        var pb = new PasswordBox
        {
            Style = PlainPasswordBox,
            Password = field.Value,
            Background = FieldTint,
            Foreground = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = field.Model.MaxLength,
        };
        pb.GotKeyboardFocus += (_, _) => { pb.Background = FieldEditing; pb.Foreground = Brushes.Black; };
        pb.LostKeyboardFocus += async (_, _) =>
        {
            string value = pb.Password;
            if (value != field.Value) await SafeSetAsync(field, value);
            pb.Background = FieldTint;
            pb.Foreground = Brushes.Transparent;
        };
        return pb;
    }

    private void ShowEditing(TextBox tb, bool editing)
    {
        if (!_items.TryGetValue(tb, out var item)) return;
        if (editing)
        {
            tb.Background = FieldEditing;
            tb.Foreground = BrushFromHex(item.Field.Model.TextColorHex);
            tb.BorderBrush = FieldOutline;
            tb.BorderThickness = new Thickness(1);
            tb.SelectAll();
        }
        else
        {
            tb.Background = tb.IsMouseOver ? FieldTintHover : FieldTint;
            tb.Foreground = Brushes.Transparent;
            tb.BorderThickness = new Thickness(0);
        }
    }

    /// <summary>Write what was typed. The box stays opaque (showing the new text) until the page
    /// has re-rendered with the value, so there's no flash of the old value in between.</summary>
    private async void Commit(TextBox tb)
    {
        if (!_items.TryGetValue(tb, out var item)) return;
        string value = tb.Text;
        if (value != item.Field.Value) await SafeSetAsync(item.Field, value);
        if (!tb.IsKeyboardFocusWithin) ShowEditing(tb, false);
    }

    private Border MakeToggle(FormFieldViewModel field, FormWidget widget)
    {
        var border = new Border
        {
            Background = FieldTint,
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
            BorderBrush = FieldOutline,
        };
        async void Toggle()
        {
            await SafeSetAsync(field, field.ToggledValue(widget));
        }
        border.MouseEnter += (_, _) => border.Background = FieldTintHover;
        border.MouseLeave += (_, _) => border.Background = FieldTint;
        border.GotKeyboardFocus += (_, _) => border.BorderThickness = new Thickness(1);
        border.LostKeyboardFocus += (_, _) => border.BorderThickness = new Thickness(0);
        border.MouseLeftButtonDown += (_, e) => { border.Focus(); e.Handled = true; };
        border.MouseLeftButtonUp += (_, e) => { Toggle(); e.Handled = true; };
        border.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter) { Toggle(); e.Handled = true; }
        };
        return border;
    }

    private Border MakeChoice(FormFieldViewModel field)
    {
        var border = new Border
        {
            Background = FieldTint,
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
            BorderBrush = FieldOutline,
        };
        // A small chevron so the field reads as a drop-down.
        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L4,4 L8,0"),
            Stroke = FieldOutline,
            StrokeThickness = 1.4,
            Width = 8,
            Height = 5,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            IsHitTestVisible = false,
        };
        border.Child = chevron;

        void OpenMenu()
        {
            var menu = new ContextMenu();
            foreach (var option in field.Model.Options)
            {
                var item = new MenuItem
                {
                    Header = option.Display.Length > 0 ? option.Display : option.Export,
                    IsCheckable = true,
                    IsChecked = option.Export == field.Value,
                };
                string export = option.Export;
                item.Click += async (_, _) => await SafeSetAsync(field, export);
                menu.Items.Add(item);
            }
            if (field.Model.Editable || field.Model.Options.Count == 0)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                var other = new MenuItem { Header = "Other…" };
                other.Click += async (_, _) =>
                {
                    string? typed = InputDialog.Show(Window.GetWindow(this)!, "Fill in", field.Hint, field.DisplayValue);
                    if (typed != null) await SafeSetAsync(field, typed);
                };
                menu.Items.Add(other);
            }
            if (!FormField.IsOff(field.Value) && field.Model.Options.Count > 0)
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "Clear" };
                clear.Click += async (_, _) => await SafeSetAsync(field, "");
                menu.Items.Add(clear);
            }
            Graphite.App.Services.MenuPlacement.Show(menu, border);
        }

        border.MouseEnter += (_, _) => border.Background = FieldTintHover;
        border.MouseLeave += (_, _) => border.Background = FieldTint;
        border.GotKeyboardFocus += (_, _) => border.BorderThickness = new Thickness(1);
        border.LostKeyboardFocus += (_, _) => border.BorderThickness = new Thickness(0);
        border.MouseLeftButtonDown += (_, e) => { border.Focus(); e.Handled = true; };
        border.MouseLeftButtonUp += (_, e) => { OpenMenu(); e.Handled = true; };
        border.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter or Key.Down) { OpenMenu(); e.Handled = true; }
        };
        return border;
    }

    private static async Task SafeSetAsync(FormFieldViewModel field, string value)
    {
        try { await field.SetAsync(value); }
        catch (Exception ex)
        {
            App.LogError($"Filling form field '{field.Name}' failed", ex);
            MessageDialog.Show(Application.Current.MainWindow, $"Couldn't fill in this field: {ex.Message}",
                "Graphite", DialogButtons.OK, DialogIcon.Warning);
        }
    }

    // ------------------------------------------------------------- layout

    private void ApplyFont(UIElement element)
    {
        if (!_items.TryGetValue(element, out var item)) return;
        double h = item.Widget.Bounds.Height;
        double pt = item.Field.Model.FontSize > 0
            ? item.Field.Model.FontSize
            : item.Field.Model.Multiline ? 10 : Math.Clamp((h - 4) / 1.15, 4, 12);
        double size = Math.Max(1, pt * Zoom);
        if (element is Control c) c.FontSize = size;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double z = Zoom;
        foreach (UIElement child in InternalChildren)
            if (_items.TryGetValue(child, out var item))
                child.Measure(new Size(Math.Max(0, item.Widget.Bounds.Width * z), Math.Max(0, item.Widget.Bounds.Height * z)));
        return new Size(0, 0); // never asks for room: it sits over the page
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double z = Zoom;
        foreach (UIElement child in InternalChildren)
            if (_items.TryGetValue(child, out var item))
            {
                var b = item.Widget.Bounds;
                child.Arrange(new Rect(b.X * z, b.Y * z, Math.Max(0, b.Width * z), Math.Max(0, b.Height * z)));
            }
        return finalSize;
    }

    // ------------------------------------------------------------- helpers

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush BrushFromHex(string hex)
    {
        try { return Frozen((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return Brushes.Black; }
    }
}

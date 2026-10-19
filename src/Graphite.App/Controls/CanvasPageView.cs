using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Graphite.Core.Canvas;

namespace Graphite.App.Controls;

/// <summary>
/// A Canvas page (the start page of a course) laid out as part of the course view: headings, paragraphs, lists,
/// quotes, code and rules, built from the blocks <see cref="CanvasHtmlBlocks"/> reads out of its HTML.
/// Only Graphite's own brushes are used (by resource reference, so a change of theme or material applies at once);
/// colours and fonts of the original page are left out on purpose. Links raise <see cref="LinkClicked"/>. Pictures are
/// not shown (they cost loading time and scrolling smoothness for little on a start page).
/// </summary>
public sealed class CanvasPageView : StackPanel
{
    public static readonly DependencyProperty BlocksProperty =
        DependencyProperty.Register(nameof(Blocks), typeof(IReadOnlyList<CanvasBlock>), typeof(CanvasPageView),
            new PropertyMetadata(null, (d, _) => ((CanvasPageView)d).Rebuild()));

    public IReadOnlyList<CanvasBlock>? Blocks
    {
        get => (IReadOnlyList<CanvasBlock>?)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>A link of the page was clicked: its absolute address.</summary>
    public event Action<string>? LinkClicked;

    private const double BodySize = 13.5;
    private static readonly double[] HeadingSizes = { 0, 24, 20, 17, 15, 14, 13.5 };
    private int _generation;

    private void Rebuild()
    {
        _generation++;
        Children.Clear();
        if (Blocks is not { Count: > 0 } blocks) return;

        bool first = true;
        foreach (var block in blocks)
        {
            if (Make(block, first) is { } element) Children.Add(element);
            first = false;
        }
    }

    private UIElement? Make(CanvasBlock block, bool first)
    {
        switch (block.Kind)
        {
            case CanvasBlockKind.Heading:
            {
                int level = Math.Clamp(block.Level, 1, 6);
                var text = Text(block, HeadingSizes[level], FontWeights.SemiBold, "App.TextBrush", forceBold: true);
                text.Margin = new Thickness(0, first ? 0 : level <= 2 ? 16 : 12, 0, 6);
                return text;
            }
            case CanvasBlockKind.ListItem:
            {
                var grid = new Grid { Margin = new Thickness(4 + block.Level * 22, 0, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var marker = new TextBlock
                {
                    Text = block.Marker,
                    FontSize = BodySize,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                marker.SetResourceReference(TextBlock.ForegroundProperty, "App.TextSecondaryBrush");
                var text = Text(block, BodySize, FontWeights.Normal, "App.TextBrush");
                Grid.SetColumn(text, 1);
                grid.Children.Add(marker);
                grid.Children.Add(text);
                return grid;
            }
            case CanvasBlockKind.Quote:
            {
                var text = Text(block, BodySize, FontWeights.Normal, "App.TextSecondaryBrush");
                var bar = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Margin = new Thickness(Math.Max(0, block.Level - 1) * 14, 0, 0, 10),
                    Child = text,
                };
                bar.SetResourceReference(Border.BorderBrushProperty, "App.StrokeBrush");
                return bar;
            }
            case CanvasBlockKind.Code:
            {
                var text = Text(block, 12.5, FontWeights.Normal, "App.TextBrush");
                text.FontFamily = new FontFamily("Consolas");
                var box = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 10),
                    Child = text,
                };
                box.SetResourceReference(Border.BackgroundProperty, "App.HoverBrush");
                return box;
            }
            case CanvasBlockKind.Rule:
            {
                var line = new Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 12) };
                line.SetResourceReference(Shape.FillProperty, "App.StrokeSoftBrush");
                return line;
            }
            case CanvasBlockKind.Image:
                return null;
            default:
            {
                var text = Text(block, BodySize, FontWeights.Normal, "App.TextBrush");
                text.Margin = new Thickness(0, 0, 0, 10);
                return text;
            }
        }
    }

    // ------------------------------------------------------------- text

    private TextBlock Text(CanvasBlock block, double size, FontWeight weight, string brushKey, bool forceBold = false)
    {
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Round(size * 1.5),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

        foreach (var run in block.Runs)
        {
            if (run.IsBreak) { tb.Inlines.Add(new LineBreak()); continue; }

            var piece = new Run(run.Text);
            if (run.Bold && !forceBold) piece.FontWeight = FontWeights.SemiBold;
            if (run.Italic) piece.FontStyle = FontStyles.Italic;
            if (run.Underline) piece.TextDecorations = TextDecorations.Underline;
            if (run.Code) piece.FontFamily = new FontFamily("Consolas");

            if (run.Href is { } href) tb.Inlines.Add(Link(piece, href));
            else tb.Inlines.Add(piece);
        }
        return tb;
    }

    private Hyperlink Link(Run content, string href)
    {
        var link = new Hyperlink(content)
        {
            ToolTip = href,
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
            Focusable = false,
        };
        link.SetResourceReference(TextElement.ForegroundProperty, "App.TextBrush");
        // the text colour is the link's colour; hovering lights it in the accent, like the other controls
        link.MouseEnter += (_, _) => link.SetResourceReference(TextElement.ForegroundProperty, "App.AccentSystemBrush");
        link.MouseLeave += (_, _) => link.SetResourceReference(TextElement.ForegroundProperty, "App.TextBrush");
        link.Click += (_, e) =>
        {
            e.Handled = true;
            LinkClicked?.Invoke(href);
        };
        return link;
    }
}

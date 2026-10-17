using System.IO;
using System.Windows.Documents;
using System.Windows.Media;
using Graphite.App.ViewModels;
using Graphite.Core;
using Graphite.Core.Annotations;

namespace Graphite.App.Services;

/// <summary>
/// Turns a PDF's markup into a study/working note for the built-in editor: one section per page,
/// each highlight quoted with the passage it covers (tinted in the highlight's colour), followed by
/// its comment and replies; text boxes and sticky notes with their text. Drawings only appear when
/// someone commented on them.
/// </summary>
public static class MarkupNotes
{
    private static readonly SolidColorBrush Muted = Frozen(Color.FromRgb(0x6B, 0x6B, 0x6B));

    /// <summary>Whether there is anything worth exporting.</summary>
    public static bool HasContent(DocumentViewModel doc) => doc.Annotations.Any(a => Include(a.Model));

    private static bool Include(Annotation a) => a.Kind switch
    {
        AnnotationKind.Highlight when !a.IsFreehand => true,
        AnnotationKind.Underline or AnnotationKind.StrikeOut => true,
        AnnotationKind.FreeText or AnnotationKind.Note => !string.IsNullOrWhiteSpace(a.Contents) || a.Replies.Count > 0,
        _ => !string.IsNullOrWhiteSpace(a.Contents) || a.Replies.Count > 0,
    };

    public static FlowDocument Build(DocumentViewModel doc, PageSettings page)
    {
        var document = DocxCodec.NewDocument(page);
        document.Blocks.Clear();

        string title = Path.GetFileNameWithoutExtension(doc.Title);
        var heading = DocStyles.NewParagraph(DocStyles.Title);
        heading.Inlines.Add(new Run($"Notes: {title}"));
        document.Blocks.Add(heading);

        var source = DocStyles.NewParagraph();
        source.Inlines.Add(new Run($"From {doc.Title} · {DateTime.Now:d MMMM yyyy}") { Foreground = Muted });
        document.Blocks.Add(source);

        int drawingsWithoutComment = 0;
        foreach (var group in doc.Annotations
                     .Select(a => a.Model)
                     .GroupBy(a => a.PageIndex)
                     .OrderBy(g => g.Key))
        {
            var items = group
                .OrderBy(a => Math.Round(a.Bounds.Top / 4))   // reading order: top to bottom,
                .ThenBy(a => a.Bounds.Left)                   // then left to right on the same line
                .ToList();
            var included = items.Where(Include).ToList();
            drawingsWithoutComment += items.Count - included.Count;
            if (included.Count == 0) continue;

            var pageHeading = DocStyles.NewParagraph(DocStyles.Heading2);
            pageHeading.Inlines.Add(new Run($"Page {group.Key + 1}"));
            document.Blocks.Add(pageHeading);

            foreach (var a in included) AddAnnotation(document, doc, a);
        }

        if (drawingsWithoutComment > 0)
        {
            var note = DocStyles.NewParagraph();
            note.Inlines.Add(new Run(drawingsWithoutComment == 1
                ? "Plus 1 drawing or shape without a comment (not listed)."
                : $"Plus {drawingsWithoutComment} drawings or shapes without comments (not listed).")
            { Foreground = Muted, FontStyle = System.Windows.FontStyles.Italic });
            document.Blocks.Add(note);
        }

        if (document.Blocks.Count == 0) document.Blocks.Add(DocStyles.NewParagraph());
        return document;
    }

    private static void AddAnnotation(FlowDocument document, DocumentViewModel doc, Annotation a)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Highlight:
            case AnnotationKind.Underline:
            case AnnotationKind.StrikeOut:
            {
                string quote = QuoteOf(doc, a);
                var p = DocStyles.NewParagraph();
                if (quote.Length > 0)
                {
                    var run = new Run($"“{quote}”");
                    if (a.Kind == AnnotationKind.Highlight) run.Background = Tint(a.ColorHex);
                    if (a.Kind == AnnotationKind.StrikeOut) run.TextDecorations = System.Windows.TextDecorations.Strikethrough;
                    if (a.Kind == AnnotationKind.Underline) run.TextDecorations = System.Windows.TextDecorations.Underline;
                    p.Inlines.Add(run);
                }
                else
                {
                    p.Inlines.Add(new Run(a.Kind switch
                    {
                        AnnotationKind.Underline => "(underlined passage — no selectable text)",
                        AnnotationKind.StrikeOut => "(struck-through passage — no selectable text)",
                        _ => "(highlighted passage — no selectable text)",
                    }) { Foreground = Muted, FontStyle = System.Windows.FontStyles.Italic });
                }
                document.Blocks.Add(p);
                AddComment(document, a.Contents);
                break;
            }

            case AnnotationKind.FreeText:
                AddLabelled(document, "Text box", a.Contents);
                break;

            case AnnotationKind.Note:
                AddLabelled(document, "Note", a.Contents);
                break;

            default:
                AddLabelled(document, a.Kind switch
                {
                    AnnotationKind.Ink => "Drawing",
                    AnnotationKind.Highlight => "Marker",
                    AnnotationKind.Square => "Rectangle",
                    AnnotationKind.Circle => "Ellipse",
                    AnnotationKind.Line => "Arrow",
                    _ => "Markup",
                }, a.Contents);
                break;
        }

        foreach (var reply in a.Replies)
        {
            if (string.IsNullOrWhiteSpace(reply.Text)) continue;
            var p = DocStyles.NewParagraph();
            p.Inlines.Add(new Run(string.IsNullOrWhiteSpace(reply.Author) ? "Reply: " : $"{reply.Author}: ")
            { FontWeight = System.Windows.FontWeights.Bold });
            p.Inlines.Add(new Run(reply.Text.Trim()));
            document.Blocks.Add(p);
        }
    }

    private static void AddComment(FlowDocument document, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var p = DocStyles.NewParagraph();
        p.Inlines.Add(new Run("Comment: ") { FontWeight = System.Windows.FontWeights.Bold });
        p.Inlines.Add(new Run(text.Trim()));
        document.Blocks.Add(p);
    }

    private static void AddLabelled(FlowDocument document, string label, string? text)
    {
        var p = DocStyles.NewParagraph();
        p.Inlines.Add(new Run(label + ": ") { FontWeight = System.Windows.FontWeights.Bold });
        p.Inlines.Add(new Run(string.IsNullOrWhiteSpace(text) ? "(no text)" : text.Trim()));
        document.Blocks.Add(p);
    }

    /// <summary>The page text under a text-markup annotation's word boxes.</summary>
    private static string QuoteOf(DocumentViewModel doc, Annotation a)
    {
        try
        {
            var rects = a.Quads.Count > 0 ? a.Quads : new List<RectD> { a.Bounds };
            var parts = rects.Select(q => doc.Index.TextInRect(a.PageIndex, q).Trim()).Where(t => t.Length > 0);
            return string.Join(" ", parts);
        }
        catch { return ""; }
    }

    /// <summary>The highlight colour mixed 35 % onto white — Word shading has no transparency.</summary>
    private static SolidColorBrush Tint(string hex)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); }
        catch { c = Color.FromRgb(0xF2, 0xC7, 0x44); }
        byte Mix(byte v) => (byte)Math.Round(255 - (255 - v) * 0.35);
        return Frozen(Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B)));
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

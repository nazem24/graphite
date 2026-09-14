using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace Graphite.Core.Ocr;

/// <summary>
/// Bakes an invisible text layer over scanned pages so the saved PDF becomes
/// searchable and selectable in any viewer. Text is drawn with a fully
/// transparent fill (alpha 0 -> ExtGState ca 0), positioned per OCR word box.
/// </summary>
public static class SearchablePdfWriter
{
    public static byte[] AddTextLayer(byte[] pdf, int pageIndex, IReadOnlyList<WordBox> words) =>
        AddTextLayers(pdf, new Dictionary<int, IReadOnlyList<WordBox>> { [pageIndex] = words });

    /// <summary>Add text layers to many pages in ONE open/save pass. Writing page by page
    /// re-parses and re-serializes the whole document per page — O(n²) for a scanned book.</summary>
    public static byte[] AddTextLayers(byte[] pdf, IReadOnlyDictionary<int, IReadOnlyList<WordBox>> pages)
    {
        Graphite.Core.Fonts.FontSetup.Ensure();
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        var fontCache = new Dictionary<double, XFont>();

        foreach (var (pageIndex, words) in pages)
        {
            if (pageIndex < 0 || pageIndex >= doc.PageCount || words.Count == 0) continue;
            var page = doc.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            foreach (var w in words)
            {
                if (string.IsNullOrWhiteSpace(w.Text) || w.Box.Height <= 0.5) continue;
                double size = Math.Max(2.0, w.Box.Height * 0.85);
                // XFont creation is expensive; a scanned page yields thousands of words
                // at only a handful of distinct sizes.
                if (!fontCache.TryGetValue(size, out var font))
                    fontCache[size] = font = new XFont("Arial", size);
                gfx.DrawString(w.Text, font, invisible,
                    new XRect(w.Box.X, w.Box.Y, Math.Max(w.Box.Width, 1), w.Box.Height),
                    XStringFormats.CenterLeft);
            }
        }

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }
}

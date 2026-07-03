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
    public static byte[] AddTextLayer(byte[] pdf, int pageIndex, IReadOnlyList<WordBox> words)
    {
        Graphite.Core.Fonts.FontSetup.Ensure();
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var page = doc.Pages[pageIndex];
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w.Text) || w.Box.Height <= 0.5) continue;
            double size = Math.Max(2.0, w.Box.Height * 0.85);
            var font = new XFont("Arial", size);
            gfx.DrawString(w.Text, font, invisible,
                new XRect(w.Box.X, w.Box.Y, Math.Max(w.Box.Width, 1), w.Box.Height),
                XStringFormats.CenterLeft);
        }

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }
}

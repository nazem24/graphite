using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Graphite.Core.Editing;

/// <summary>
/// Direct content edits. PDF has no reflowable text model, so text editing is
/// implemented the way most editors do it under the hood: the original region is
/// covered and replacement content is appended to the page's content stream.
/// All coordinates are PDF points, top-left origin (XGraphics' native space).
/// </summary>
public static class ContentEditor
{
    /// <summary>Replace the text under <paramref name="area"/> with new text.</summary>
    public static byte[] ReplaceText(byte[] pdf, int pageIndex, RectD area, string newText,
        string fontFamily = "Segoe UI", double fontSize = 11,
        string textColorHex = "#1A1A1A", string coverColorHex = "#FFFFFF")
    {
        Graphite.Core.Fonts.FontSetup.Ensure();
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        var page = doc.Pages[pageIndex];
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

        gfx.DrawRectangle(new XSolidBrush(ParseX(coverColorHex)),
            new XRect(area.X, area.Y, area.Width, area.Height));

        if (!string.IsNullOrEmpty(newText))
        {
            var font = new XFont(fontFamily, fontSize);
            var tf = new PdfSharp.Drawing.Layout.XTextFormatter(gfx);
            tf.DrawString(newText, font, new XSolidBrush(ParseX(textColorHex)),
                new XRect(area.X, area.Y, Math.Max(area.Width, 10), Math.Max(area.Height, fontSize * 1.4)),
                XStringFormats.TopLeft);
        }

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    /// <summary>Cover (white-out / redact visually) a region.</summary>
    public static byte[] CoverArea(byte[] pdf, int pageIndex, RectD area, string coverColorHex = "#FFFFFF") =>
        ReplaceText(pdf, pageIndex, area, "", coverColorHex: coverColorHex);

    /// <summary>Place an image file onto the page at the given rectangle.</summary>
    public static byte[] PlaceImage(byte[] pdf, int pageIndex, RectD area, string imagePath)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        var page = doc.Pages[pageIndex];
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        using var img = XImage.FromFile(imagePath);
        gfx.DrawImage(img, new XRect(area.X, area.Y, area.Width, area.Height));

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static XColor ParseX(string hex)
    {
        var (r, g, b) = Graphite.Core.Annotations.Annotation.ParseColor(hex);
        return XColor.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }
}

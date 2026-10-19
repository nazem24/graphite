using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Graphite.Core.Editing;

/// <summary>
/// Direct content edits. PDF has no reflowable text model, so text editing is
/// implemented the way most editors do it under the hood: the original region is
/// covered and replacement content is appended to the page's content stream.
/// All coordinates are PDF points in display space: top-left origin, the page as it is shown
/// (rotation and CropBox applied — see <see cref="Graphite.Core.Pdf.PageGeometry"/>).
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
        gfx.MultiplyTransform(Graphite.Core.Pdf.PageGeometry.DisplayTransform(page));

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

    /// <summary>How replacement text is typeset: taken from the original text so the edit blends in.</summary>
    /// <param name="BaselineY">Baseline of the first line (display space), or null to start at the top of the area.</param>
    public sealed record TextEditStyle(string FontFamily, double FontSize, bool Bold, bool Italic,
        string TextColorHex, double? BaselineY = null);

    /// <summary>Replace the text under <paramref name="area"/>, typeset like the original: its font size, family,
    /// weight, colour and baseline. The region is covered with the colour of the page behind the text (sampled
    /// from a render, so it works on tinted and shaded backgrounds) unless <paramref name="coverColorHex"/> says otherwise.</summary>
    public static byte[] ReplaceText(byte[] pdf, int pageIndex, RectD area, string newText,
        TextEditStyle style, string? coverColorHex = null)
    {
        Graphite.Core.Fonts.FontSetup.Ensure();
        coverColorHex ??= SampleBackground(pdf, pageIndex, area) ?? "#FFFFFF";

        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        var page = doc.Pages[pageIndex];
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        gfx.MultiplyTransform(Graphite.Core.Pdf.PageGeometry.DisplayTransform(page));

        // A hair larger than the text box so anti-aliased glyph edges are covered too.
        var cover = area.Inflate(1.2);
        gfx.DrawRectangle(new XSolidBrush(ParseX(coverColorHex)), new XRect(cover.X, cover.Y, cover.Width, cover.Height));

        if (!string.IsNullOrEmpty(newText))
        {
            var fontStyle = (style.Bold, style.Italic) switch
            {
                (true, true) => XFontStyleEx.BoldItalic,
                (true, false) => XFontStyleEx.Bold,
                (false, true) => XFontStyleEx.Italic,
                _ => XFontStyleEx.Regular,
            };
            var font = new XFont(style.FontFamily, style.FontSize, fontStyle);
            var brush = new XSolidBrush(ParseX(style.TextColorHex));

            if (style.BaselineY is { } baseline && !newText.Contains('\n'))
            {
                // One line: sit on the original baseline and run on as far as the text needs.
                gfx.DrawString(newText, font, brush, new XPoint(area.X, baseline), XStringFormats.BaseLineLeft);
            }
            else
            {
                var tf = new PdfSharp.Drawing.Layout.XTextFormatter(gfx);
                tf.DrawString(newText, font, brush,
                    new XRect(area.X, area.Y, Math.Max(area.Width, 10), Math.Max(area.Height, style.FontSize * 1.4)),
                    XStringFormats.TopLeft);
            }
        }

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    /// <summary>The colour of the page behind a region: the commonest colour in a render of it (text pixels are the
    /// minority, so the background wins). Null when the page cannot be rendered.</summary>
    public static string? SampleBackground(byte[] pdf, int pageIndex, RectD area)
    {
        try
        {
            const double scale = 2.0;
            var renderer = new Graphite.Core.Rendering.PdfRenderer(pdf);
            var img = renderer.Render(pageIndex, scale, withAnnotations: false);

            int x0 = Math.Clamp((int)Math.Floor((area.Left - 1) * scale), 0, img.Width - 1);
            int x1 = Math.Clamp((int)Math.Ceiling((area.Right + 1) * scale), x0 + 1, img.Width);
            int y0 = Math.Clamp((int)Math.Floor((area.Top - 1) * scale), 0, img.Height - 1);
            int y1 = Math.Clamp((int)Math.Ceiling((area.Bottom + 1) * scale), y0 + 1, img.Height);

            // Histogram of 4-bit-per-channel colours, keeping the running sum of each bin so the winner can be averaged.
            var count = new int[4096];
            var sumR = new long[4096];
            var sumG = new long[4096];
            var sumB = new long[4096];
            for (int y = y0; y < y1; y++)
            {
                int row = y * img.Width * 4;
                for (int x = x0; x < x1; x++)
                {
                    int i = row + x * 4;
                    int b = img.Bgra[i], g = img.Bgra[i + 1], r = img.Bgra[i + 2];
                    int bin = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    count[bin]++; sumR[bin] += r; sumG[bin] += g; sumB[bin] += b;
                }
            }
            int best = 0;
            for (int i = 1; i < count.Length; i++) if (count[i] > count[best]) best = i;
            if (count[best] == 0) return null;
            int n = count[best];
            return $"#{sumR[best] / n:X2}{sumG[best] / n:X2}{sumB[best] / n:X2}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Background sampling failed: {ex.Message}");
            return null;
        }
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
        gfx.MultiplyTransform(Graphite.Core.Pdf.PageGeometry.DisplayTransform(page));
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

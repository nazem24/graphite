using PDFtoImage;
using SkiaSharp;

namespace Graphite.Core.Rendering;

/// <summary>
/// PDFium-backed rasterizer. Thread-safe (PDFium itself is single-threaded,
/// so all render calls are serialized behind a lock).
/// </summary>
public sealed class PdfRenderer
{
    private static readonly object PdfiumLock = new();

    private byte[] _pdf;
    public int PageCount { get; private set; }

    /// <summary>Render pages with inverted colors (dark reading mode).</summary>
    public bool Invert { get; set; }

    /// <summary>Page sizes in PDF points (1/72"), rotation applied.</summary>
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; private set; } = Array.Empty<(double, double)>();

    public PdfRenderer(byte[] pdfBytes) => _pdf = Load(pdfBytes);

    public void Reload(byte[] pdfBytes) => _pdf = Load(pdfBytes);

    private byte[] Load(byte[] pdfBytes)
    {
        lock (PdfiumLock)
        {
            PageCount = Conversion.GetPageCount(pdfBytes);
            var sizes = Conversion.GetPageSizes(pdfBytes);
            PageSizes = sizes.Select(s => ((double)s.Width, (double)s.Height)).ToList();
        }
        return pdfBytes;
    }

    /// <summary>Render one page at the given scale (1.0 = 72 dpi = 1pt : 1px).</summary>
    public RenderedPage Render(int pageIndex, double scale, bool withAnnotations = true)
    {
        var (w, h) = PageSizes[pageIndex];
        int pxW = Math.Max(1, (int)Math.Round(w * scale));
        int pxH = Math.Max(1, (int)Math.Round(h * scale));

        lock (PdfiumLock)
        {
            using SKBitmap bmp = Conversion.ToImage(
                _pdf,
                page: (Index)pageIndex,
                options: new RenderOptions(
                    Width: pxW,
                    Height: pxH,
                    WithAnnotations: withAnnotations,
                    WithFormFill: true,
                    BackgroundColor: SKColors.White,
                    AntiAliasing: PdfAntiAliasing.All));

            // Only make a converted copy when the decode didn't already give us Bgra8888 —
            // PDFium normally does on this platform, so this avoids doubling up a full
            // page-sized buffer (easily tens of MB at high zoom) on every single render.
            using SKBitmap? converted = bmp.ColorType == SKColorType.Bgra8888 ? null : bmp.Copy(SKColorType.Bgra8888);
            SKBitmap target = converted ?? bmp;

            // SKBitmap.Bytes already hands back a fresh managed copy of the pixels, so use
            // it directly (a .ToArray() on top would double-copy a page-sized buffer), and
            // run the dark-mode invert on that managed copy instead of marshalling the
            // native pixels out and back a second time.
            byte[] pixels = target.Bytes;
            if (Invert) InvertBgra(pixels);

            return new RenderedPage(target.Width, target.Height, pixels);
        }
    }

    /// <summary>Render one page and encode it (png/jpeg/webp).</summary>
    public byte[] RenderEncoded(int pageIndex, double scale, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 95)
    {
        var (w, h) = PageSizes[pageIndex];
        int pxW = Math.Max(1, (int)Math.Round(w * scale));
        int pxH = Math.Max(1, (int)Math.Round(h * scale));

        lock (PdfiumLock)
        {
            using SKBitmap bmp = Conversion.ToImage(
                _pdf,
                page: (Index)pageIndex,
                options: new RenderOptions(
                    Width: pxW,
                    Height: pxH,
                    WithAnnotations: true,
                    WithFormFill: true,
                    BackgroundColor: SKColors.White));
            using var data = bmp.Encode(format, quality);
            return data.ToArray();
        }
    }

    // Precomputed dimmed-invert lookup: v -> 235 - v * 235 / 255 (built once, not per render).
    private static readonly byte[] InvertLut = BuildInvertLut();

    private static byte[] BuildInvertLut()
    {
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)(235 - v * 235 / 255);
        return lut;
    }

    /// <summary>Invert RGB, slightly dimmed so pages read as dark gray rather than pitch black.</summary>
    private static void InvertBgra(byte[] buf)
    {
        var lut = InvertLut;
        for (int i = 0; i + 3 < buf.Length; i += 4)
        {
            // BGRA, premultiplied; pages are rendered opaque so A == 255.
            buf[i] = lut[buf[i]];
            buf[i + 1] = lut[buf[i + 1]];
            buf[i + 2] = lut[buf[i + 2]];
        }
    }
}

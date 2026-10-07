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
            // One pass over the file: the size list is also the page count. This used to ask
            // PDFium for the count and then for the sizes, parsing the whole document twice.
            var sizes = Conversion.GetPageSizes(pdfBytes).Select(s => ((double)s.Width, (double)s.Height)).ToList();
            PageSizes = sizes;
            PageCount = sizes.Count;
        }
        return pdfBytes;
    }

    /// <summary>Receives a rendered page's pixels (BGRA8888, premultiplied) straight from
    /// native memory. The pointer is only valid for the duration of the call.</summary>
    public delegate T PixelSink<out T>(int width, int height, IntPtr pixels, int bufferSize, int stride);

    /// <summary>Render one page at the given scale (1.0 = 72 dpi = 1pt : 1px).</summary>
    public RenderedPage Render(int pageIndex, double scale, bool withAnnotations = true) =>
        Render(pageIndex, scale, withAnnotations, static (w, h, ptr, size, stride) =>
        {
            // Managed copy for callers that want a plain byte[] (print, tests). Rows are
            // tightly packed for BGRA8888, but copy row-by-row in case Skia pads them.
            var bytes = new byte[w * h * 4];
            if (stride == w * 4)
                System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, bytes.Length);
            else
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(ptr + y * stride, bytes, y * w * 4, w * 4);
            return new RenderedPage(w, h, bytes);
        });

    /// <summary>Render one page and hand its native pixel buffer to <paramref name="sink"/>.
    /// Lets the UI build its bitmap directly from PDFium's output instead of going through
    /// an intermediate managed byte[] — that copy was a full page-sized Large Object Heap
    /// allocation (tens of MB at high zoom) on every render, which drove frequent gen-2 GCs.</summary>
    public T Render<T>(int pageIndex, double scale, bool withAnnotations, PixelSink<T> sink)
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
            // page-sized buffer on every single render.
            using SKBitmap? converted = bmp.ColorType == SKColorType.Bgra8888 ? null : bmp.Copy(SKColorType.Bgra8888);
            SKBitmap target = converted ?? bmp;

            IntPtr pixels = target.GetPixels();
            int stride = target.RowBytes;
            int size = stride * target.Height;
            // Dark-mode invert runs in place on the native buffer (no marshalling round trip).
            if (Invert) InvertBgra(pixels, size);

            return sink(target.Width, target.Height, pixels, size, stride);
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
    private static unsafe void InvertBgra(IntPtr ptr, int length)
    {
        byte* buf = (byte*)ptr;
        var lut = InvertLut;
        // A full-page buffer at high zoom is tens of MB — partition the LUT pass across
        // cores once it's big enough to matter (boundaries stay 4-byte aligned for BGRA).
        if (length >= 4 * 1024 * 1024)
        {
            int chunk = ((length / Environment.ProcessorCount) + 3) & ~3;
            Parallel.For(0, (length + chunk - 1) / chunk, c =>
            {
                byte* b = (byte*)ptr;
                int start = c * chunk;
                int end = Math.Min(length, start + chunk);
                for (int i = start; i + 3 < end; i += 4)
                {
                    b[i] = lut[b[i]];
                    b[i + 1] = lut[b[i + 1]];
                    b[i + 2] = lut[b[i + 2]];
                }
            });
            return;
        }
        for (int i = 0; i + 3 < length; i += 4)
        {
            // BGRA, premultiplied; pages are rendered opaque so A == 255.
            buf[i] = lut[buf[i]];
            buf[i + 1] = lut[buf[i + 1]];
            buf[i + 2] = lut[buf[i + 2]];
        }
    }
}

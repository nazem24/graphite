using Graphite.Core.Rendering;
using SkiaSharp;

namespace Graphite.Core.Export;

public static class ImageExporter
{
    /// <summary>Export pages as image files: {basePath}-page-N.{ext}. Returns written paths.</summary>
    public static List<string> Export(PdfRenderer renderer, IReadOnlyList<int> pageIndices,
        string basePath, string format = "png", int dpi = 150,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var (fmt, ext) = format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => (SKEncodedImageFormat.Jpeg, "jpg"),
            "webp" => (SKEncodedImageFormat.Webp, "webp"),
            _ => (SKEncodedImageFormat.Png, "png"),
        };

        var written = new List<string>();
        double scale = dpi / 72.0;
        int done = 0;
        foreach (int i in pageIndices)
        {
            ct.ThrowIfCancellationRequested();
            byte[] data = renderer.RenderEncoded(i, scale, fmt);
            string path = pageIndices.Count == 1
                ? $"{basePath}.{ext}"
                : $"{basePath}-page-{i + 1}.{ext}";
            File.WriteAllBytes(path, data);
            written.Add(path);
            progress?.Report(++done);
        }
        return written;
    }
}

using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Graphite.Core.Rendering;

namespace Graphite.App.Services;

/// <summary>A rendered first page and the document's page count.</summary>
public sealed record ThumbnailResult(ImageSource Image, int PageCount);

/// <summary>
/// Renders small first-page previews for the start screen's library cards. Rendering is
/// throttled (two at a time), cancellable when the person navigates away, and cached in memory
/// until the file changes. It only ever reads files the caller says are on this PC — online-only
/// OneDrive placeholders are skipped by the callers so browsing never triggers a download.
/// </summary>
public static class ThumbnailService
{
    private const long MaxFileBytes = 120L * 1024 * 1024; // don't read huge scans just for a preview
    private const int MaxCached = 400;

    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly ConcurrentDictionary<string, ThumbnailResult?> Cache = new();

    private static string Key(string path, long size, DateTime modifiedUtc) =>
        $"{path.ToLowerInvariant()}|{size}|{modifiedUtc.Ticks}";

    /// <summary>The preview for a PDF, or null when it cannot be rendered (encrypted, damaged,
    /// too large, cancelled).</summary>
    public static async Task<ThumbnailResult?> GetAsync(string path, long size, DateTime modifiedUtc,
        int targetWidth, CancellationToken ct)
    {
        string key = Key(path, size, modifiedUtc);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (size > MaxFileBytes) return null;

        try { await Gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            if (ct.IsCancellationRequested) return null;
            if (Cache.TryGetValue(key, out cached)) return cached;

            var result = await Task.Run(() => Render(path, targetWidth), CancellationToken.None).ConfigureAwait(false);
            if (Cache.Count > MaxCached) Cache.Clear();
            Cache[key] = result;
            return result;
        }
        finally { Gate.Release(); }
    }

    private static ThumbnailResult? Render(string path, int targetWidth)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var renderer = new PdfRenderer(bytes);
            if (renderer.PageCount == 0) return new ThumbnailResult(EmptyImage(), 0);

            double pageWidth = Math.Max(1, renderer.PageSizes[0].Width);
            double scale = Math.Clamp(targetWidth / pageWidth, 0.1, 2.0);
            var page = renderer.Render(0, scale, withAnnotations: false);

            var bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96,
                PixelFormats.Pbgra32, null, page.Bgra, page.Width * 4);
            bitmap.Freeze(); // usable from the UI thread
            return new ThumbnailResult(bitmap, renderer.PageCount);
        }
        catch (Exception ex)
        {
            // Password-protected, damaged or not really a PDF: the card shows a plain paper.
            System.Diagnostics.Debug.WriteLine($"Thumbnail skipped for {path}: {ex.Message}");
            return null;
        }
    }

    private static ImageSource EmptyImage()
    {
        var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Forget a file's cached preview (it was saved or replaced).</summary>
    public static void Invalidate(string path)
    {
        string prefix = path.ToLowerInvariant() + "|";
        foreach (var k in Cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            Cache.TryRemove(k, out _);
    }
}

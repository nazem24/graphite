using Tesseract;

namespace Graphite.Core.Ocr;

public sealed record OcrWord(string Text, RectD BoxPx, float Confidence);
public sealed record OcrPageResult(string FullText, IReadOnlyList<OcrWord> Words, int PixelWidth, int PixelHeight);

/// <summary>
/// Tesseract-backed OCR. Requires a "tessdata" folder next to the executable
/// containing at least one *.traineddata file (see tools/get-tessdata.ps1).
/// By default every installed language is loaded together (e.g. "eng+deu"), so
/// mixed English/German documents are recognized in one pass.
/// </summary>
public sealed class OcrEngine : IDisposable
{
    private readonly TesseractEngine _engine;

    public static string DefaultDataPath =>
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    /// <summary>Language codes of all *.traineddata files in the data folder
    /// (English first, then alphabetical). The orientation-detection file "osd" is excluded.</summary>
    public static IReadOnlyList<string> InstalledLanguages(string? dataPath = null)
    {
        string dir = dataPath ?? DefaultDataPath;
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.traineddata")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(l => !string.Equals(l, "osd", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l == "eng" ? 0 : 1)
            .ThenBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsAvailable(string? dataPath = null) =>
        InstalledLanguages(dataPath).Count > 0;

    /// <param name="language">Tesseract language string such as "eng+deu"; null = all installed languages.</param>
    public OcrEngine(string? dataPath = null, string? language = null)
    {
        language ??= string.Join("+", InstalledLanguages(dataPath));
        _engine = new TesseractEngine(dataPath ?? DefaultDataPath, language, EngineMode.Default);
    }

    /// <summary>Recognize a page image (PNG/JPEG bytes).</summary>
    public OcrPageResult RecognizeImage(byte[] encodedImage)
    {
        using var pix = Pix.LoadFromMemory(encodedImage);
        using var page = _engine.Process(pix);

        var words = new List<OcrWord>();
        using (var iter = page.GetIterator())
        {
            iter.Begin();
            do
            {
                string text = iter.GetText(PageIteratorLevel.Word)?.Trim() ?? "";
                if (text.Length > 0 && iter.TryGetBoundingBox(PageIteratorLevel.Word, out var r))
                {
                    words.Add(new OcrWord(
                        text,
                        new RectD(r.X1, r.Y1, r.Width, r.Height),
                        iter.GetConfidence(PageIteratorLevel.Word)));
                }
            } while (iter.Next(PageIteratorLevel.Word));
        }

        return new OcrPageResult(page.GetText(), words, pix.Width, pix.Height);
    }

    /// <summary>Convert pixel-space OCR words to page space (points, top-left origin).</summary>
    public static IEnumerable<WordBox> ToPageSpace(OcrPageResult result, double pageWidthPt, double pageHeightPt)
    {
        double sx = pageWidthPt / result.PixelWidth;
        double sy = pageHeightPt / result.PixelHeight;
        foreach (var w in result.Words)
            yield return new WordBox(w.Text,
                new RectD(w.BoxPx.X * sx, w.BoxPx.Y * sy, w.BoxPx.Width * sx, w.BoxPx.Height * sy));
    }

    public void Dispose() => _engine.Dispose();
}

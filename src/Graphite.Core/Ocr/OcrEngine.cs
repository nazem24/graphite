using Tesseract;

namespace Graphite.Core.Ocr;

public sealed record OcrWord(string Text, RectD BoxPx, float Confidence);
public sealed record OcrPageResult(string FullText, IReadOnlyList<OcrWord> Words, int PixelWidth, int PixelHeight);

/// <summary>
/// Tesseract-backed OCR. Requires a "tessdata" folder next to the executable
/// containing at least eng.traineddata (see tools/get-tessdata.ps1).
/// </summary>
public sealed class OcrEngine : IDisposable
{
    private readonly TesseractEngine _engine;

    public static string DefaultDataPath =>
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    public static bool IsAvailable(string? dataPath = null, string language = "eng") =>
        File.Exists(Path.Combine(dataPath ?? DefaultDataPath, $"{language}.traineddata"));

    public OcrEngine(string? dataPath = null, string language = "eng")
    {
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

using System.Text;
using Graphite.Core.Annotations;
using Graphite.Core.Rendering;
using Graphite.Core.Text;

namespace Graphite.App.Services;

/// <summary>
/// Pays the one-off cost of opening the first document while the app is starting and the
/// user is still choosing a file: loading the PDFium / Skia native libraries, JIT-compiling the
/// PDF readers, parsing PdfPig's standard-font tables. Without this every one of those lands
/// on the first Open, which is why the first document used to be so much slower than the next.
/// Runs on a low-priority background thread and never throws.
/// </summary>
public static class Warmup
{
    public static void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Graphite warm-up",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    private static void Run()
    {
        try
        {
            byte[] pdf = BuildTinyPdf();

            var renderer = new PdfRenderer(pdf);
            renderer.Render(0, 1.0, false);            // PDFium + Skia: natives, form-fill setup

            using var index = new TextIndex(pdf);        // PdfPig: parser, fonts, word extraction
            index.GetPage(0);
            index.GetOutline();

            AnnotationCodec.Read(pdf);                    // PdfSharp: reader
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warm-up skipped: {ex.Message}");
        }
    }

    /// <summary>A valid one-page PDF with a line of text, built in memory (xref offsets and all).</summary>
    private static byte[] BuildTinyPdf()
    {
        const string content = "BT /F1 18 Tf 20 100 Td (Graphite) Tj ET";
        string[] objects =
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<</Size ").Append(objects.Length + 1).Append("/Root 1 0 R>>\n")
          .Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Graphite.Core.Text;

namespace Graphite.Core.Export;

/// <summary>PDF -> .docx. Reconstructs paragraphs from positioned words (layout is approximate).</summary>
public static class WordExporter
{
    public static void Export(TextIndex index, string outputPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();

        for (int p = 0; p < index.PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = index.GetPage(p);
            var lines = TextLayout.GetLines(page);

            foreach (var para in TextLayout.GetParagraphs(lines))
            {
                var text = string.Join(" ", para.Select(l => l.Text));
                if (string.IsNullOrWhiteSpace(text)) continue;
                body.Append(new Paragraph(new Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text(text) { Space = SpaceProcessingModeValues.Preserve })));
            }

            if (p < index.PageCount - 1)
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

            progress?.Report(p + 1);
        }

        main.Document = new Document(body);
        main.Document.Save();
    }
}

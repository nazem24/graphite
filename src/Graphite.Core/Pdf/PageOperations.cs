using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Graphite.Core.Pdf;

/// <summary>
/// Structural page surgery. All operations are pure: bytes in, bytes out.
/// The caller (DocumentViewModel) swaps its working buffer and reloads renderer/index.
/// </summary>
public static class PageOperations
{
    private static PdfDocument OpenImport(byte[] pdf) =>
        PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);

    private static PdfDocument OpenModify(byte[] pdf) =>
        PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);

    private static byte[] SaveToBytes(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    public static byte[] Merge(IEnumerable<byte[]> pdfs)
    {
        var output = new PdfDocument();
        foreach (var pdf in pdfs)
        {
            using var src = OpenImport(pdf);
            for (int i = 0; i < src.PageCount; i++)
                output.AddPage(src.Pages[i]);
        }
        return SaveToBytes(output);
    }

    public static byte[] MergeFiles(IEnumerable<string> paths) =>
        Merge(paths.Select(File.ReadAllBytes));

    /// <summary>Split into single-page PDFs. Returns one byte[] per page.</summary>
    public static List<byte[]> SplitEachPage(byte[] pdf)
    {
        using var src = OpenImport(pdf);
        var result = new List<byte[]>(src.PageCount);
        for (int i = 0; i < src.PageCount; i++)
        {
            var one = new PdfDocument();
            one.AddPage(src.Pages[i]);
            result.Add(SaveToBytes(one));
        }
        return result;
    }

    /// <summary>Extract the given 0-based pages into a new PDF (order preserved as given).</summary>
    public static byte[] ExtractPages(byte[] pdf, IReadOnlyList<int> pageIndices)
    {
        using var src = OpenImport(pdf);
        var output = new PdfDocument();
        foreach (int i in pageIndices)
            output.AddPage(src.Pages[i]);
        return SaveToBytes(output);
    }

    public static byte[] DeletePages(byte[] pdf, IReadOnlyCollection<int> pageIndices)
    {
        using var doc = OpenModify(pdf);
        foreach (int i in pageIndices.Distinct().OrderByDescending(i => i))
            doc.Pages.RemoveAt(i);
        return SaveToBytes(doc);
    }

    /// <summary>Rotate a page by delta degrees (multiples of 90).</summary>
    public static byte[] RotatePage(byte[] pdf, int pageIndex, int deltaDegrees)
    {
        using var doc = OpenModify(pdf);
        var page = doc.Pages[pageIndex];
        page.Rotate = ((page.Rotate + deltaDegrees) % 360 + 360) % 360;
        return SaveToBytes(doc);
    }

    public static byte[] MovePage(byte[] pdf, int fromIndex, int toIndex)
    {
        using var doc = OpenModify(pdf);
        doc.Pages.MovePage(fromIndex, toIndex);
        return SaveToBytes(doc);
    }

    /// <summary>Insert all pages of another PDF at the given 0-based position.</summary>
    public static byte[] InsertPdf(byte[] pdf, int atIndex, byte[] other)
    {
        using var doc = OpenModify(pdf);
        using var src = OpenImport(other);
        for (int i = 0; i < src.PageCount; i++)
            doc.Pages.Insert(atIndex + i, src.Pages[i]);
        return SaveToBytes(doc);
    }

    public static byte[] InsertBlankPage(byte[] pdf, int atIndex, double widthPt = 595, double heightPt = 842)
    {
        using var doc = OpenModify(pdf);
        var page = new PdfPage(doc)
        {
            Width = PdfSharp.Drawing.XUnit.FromPoint(widthPt),
            Height = PdfSharp.Drawing.XUnit.FromPoint(heightPt),
        };
        doc.Pages.Insert(atIndex, page);
        return SaveToBytes(doc);
    }

    /// <summary>Parse "1,3-5,8" (1-based) into 0-based indices. Throws FormatException on bad input.</summary>
    public static List<int> ParsePageRanges(string ranges, int pageCount)
    {
        var result = new List<int>();
        foreach (var part in ranges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length == 1)
            {
                int p = int.Parse(bounds[0]) - 1;
                if (p < 0 || p >= pageCount) throw new FormatException($"Page {bounds[0]} is out of range.");
                result.Add(p);
            }
            else if (bounds.Length == 2)
            {
                int a = int.Parse(bounds[0]) - 1, b = int.Parse(bounds[1]) - 1;
                if (a < 0 || b >= pageCount || a > b) throw new FormatException($"Range '{part}' is invalid.");
                for (int i = a; i <= b; i++) result.Add(i);
            }
            else throw new FormatException($"Cannot parse '{part}'.");
        }
        return result;
    }
}

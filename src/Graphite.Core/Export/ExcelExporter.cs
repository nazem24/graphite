using ClosedXML.Excel;
using Graphite.Core.Text;

namespace Graphite.Core.Export;

/// <summary>PDF -> .xlsx. One worksheet per page; columns inferred from horizontal gaps.</summary>
public static class ExcelExporter
{
    public static void Export(TextIndex index, string outputPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        using var wb = new XLWorkbook();

        for (int p = 0; p < index.PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var ws = wb.Worksheets.Add($"Page {p + 1}");
            var lines = TextLayout.GetLines(index.GetPage(p));

            int row = 1;
            foreach (var line in lines)
            {
                var cells = TextLayout.SplitIntoCells(line);
                for (int c = 0; c < cells.Count; c++)
                {
                    // Numbers become numbers, everything else stays text.
                    if (double.TryParse(cells[c].Replace(",", "").Replace("$", "").Replace("%", ""),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double num)
                        && cells[c].Any(char.IsDigit))
                        ws.Cell(row, c + 1).Value = num;
                    else
                        ws.Cell(row, c + 1).Value = cells[c];
                }
                row++;
            }

            ws.Columns().AdjustToContents(1, Math.Min(row, 200));
            progress?.Report(p + 1);
        }

        wb.SaveAs(outputPath);
    }
}

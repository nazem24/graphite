using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Graphite.Core.Export;

/// <summary>
/// Word/Excel/PowerPoint -> PDF via COM automation (uses the locally installed
/// Microsoft Office). Late-bound so no interop assemblies are required and the
/// app still runs on machines without Office.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OfficeToPdf
{
    public static bool CanConvert(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".doc" or ".docx" or ".rtf" or ".xls" or ".xlsx" or ".ppt" or ".pptx";

    /// <summary>Convert an Office document to PDF. Throws InvalidOperationException if Office is missing.</summary>
    public static void Convert(string inputPath, string outputPdfPath)
    {
        string ext = Path.GetExtension(inputPath).ToLowerInvariant();
        switch (ext)
        {
            case ".doc" or ".docx" or ".rtf": ConvertWord(inputPath, outputPdfPath); break;
            case ".xls" or ".xlsx": ConvertExcel(inputPath, outputPdfPath); break;
            case ".ppt" or ".pptx": ConvertPowerPoint(inputPath, outputPdfPath); break;
            default: throw new NotSupportedException($"Cannot convert {ext} to PDF.");
        }
    }

    private static dynamic CreateApp(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException(
                $"Microsoft Office ({progId}) is not installed. Converting Office files to PDF requires Office.");
        return Activator.CreateInstance(type)!;
    }

    private static void ConvertWord(string input, string output)
    {
        dynamic app = CreateApp("Word.Application");
        try
        {
            app.Visible = false;
            dynamic doc = app.Documents.Open(input, ReadOnly: true);
            try { doc.ExportAsFixedFormat(output, 17 /* wdExportFormatPDF */); }
            finally { doc.Close(false); }
        }
        finally { app.Quit(); Release(app); }
    }

    private static void ConvertExcel(string input, string output)
    {
        dynamic app = CreateApp("Excel.Application");
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            dynamic wb = app.Workbooks.Open(input, ReadOnly: true);
            try { wb.ExportAsFixedFormat(0 /* xlTypePDF */, output); }
            finally { wb.Close(false); }
        }
        finally { app.Quit(); Release(app); }
    }

    private static void ConvertPowerPoint(string input, string output)
    {
        dynamic app = CreateApp("PowerPoint.Application");
        try
        {
            dynamic pres = app.Presentations.Open(input, ReadOnly: -1, Untitled: 0, WithWindow: 0);
            try { pres.ExportAsFixedFormat(output, 2 /* ppFixedFormatTypePDF */); }
            finally { pres.Close(); }
        }
        finally { app.Quit(); Release(app); }
    }

    private static void Release(object com)
    {
        try { Marshal.FinalReleaseComObject(com); } catch { /* best effort */ }
    }
}

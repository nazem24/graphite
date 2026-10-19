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
        if (ext is not (".doc" or ".docx" or ".rtf" or ".xls" or ".xlsx" or ".ppt" or ".pptx"))
            throw new NotSupportedException($"Cannot convert {ext} to PDF.");

        // Export beside the target first and move it into place when Office is done: a conversion that
        // fails or is killed halfway used to leave a broken PDF, or overwrite a good existing one.
        string temp = outputPdfPath + "." + System.Guid.NewGuid().ToString("N")[..8] + ".tmp.pdf";
        try
        {
            switch (ext)
            {
                case ".doc" or ".docx" or ".rtf": ConvertWord(inputPath, temp); break;
                case ".xls" or ".xlsx": ConvertExcel(inputPath, temp); break;
                default: ConvertPowerPoint(inputPath, temp); break;
            }
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new IOException("Office did not produce a PDF.");
            File.Move(temp, outputPdfPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static dynamic CreateApp(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException(
                $"Microsoft Office ({progId}) is not installed. Converting Office files to PDF requires Office.");
        return Activator.CreateInstance(type)!;
    }

    /// <summary>msoAutomationSecurityForceDisable — never run macros from a document
    /// the user merely wants to view as PDF.</summary>
    private const int AutomationSecurityForceDisable = 3;

    private static void ConvertWord(string input, string output)
    {
        dynamic app = CreateApp("Word.Application");
        try
        {
            app.Visible = false;
            // No dialogs: an invisible Word waiting on a repair / conversion / password prompt nobody can
            // see would hang the open forever.
            try { app.DisplayAlerts = 0; /* wdAlertsNone */ } catch { }
            try { app.AutomationSecurity = AutomationSecurityForceDisable; } catch { /* older Word */ }
            dynamic doc = app.Documents.Open(input, ConfirmConversions: false, ReadOnly: true,
                AddToRecentFiles: false, NoEncodingDialog: true);
            try { doc.ExportAsFixedFormat(output, 17 /* wdExportFormatPDF */); }
            finally { doc.Close(false); }
        }
        finally { Quit(app); Release(app); }
    }

    private static void ConvertExcel(string input, string output)
    {
        dynamic app = CreateApp("Excel.Application");
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            try { app.AutomationSecurity = AutomationSecurityForceDisable; } catch { /* older Excel */ }
            dynamic wb = app.Workbooks.Open(input, ReadOnly: true);
            try { wb.ExportAsFixedFormat(0 /* xlTypePDF */, output); }
            finally { wb.Close(false); }
        }
        finally { Quit(app); Release(app); }
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
        finally { Quit(app); Release(app); }
    }

    private static void Quit(dynamic app)
    {
        try { app.Quit(); } catch { /* already gone — Release below still runs */ }
    }

    private static void Release(object com)
    {
        try { Marshal.FinalReleaseComObject(com); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"COM release failed: {ex.Message}"); }
    }
}

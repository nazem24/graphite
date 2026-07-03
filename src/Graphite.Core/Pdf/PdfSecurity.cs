using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace Graphite.Core.Pdf;

/// <summary>Password protection and document metadata (pure bytes in → bytes out).</summary>
public static class PdfSecurity
{
    /// <summary>True if the file cannot be opened without a password.</summary>
    public static bool IsPasswordProtected(byte[] pdf)
    {
        try
        {
            using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
            return false;
        }
        catch (PdfReaderException) { return true; }
        catch (NotSupportedException) { return true; }
        catch { return false; } // some other problem — let the normal open surface it
    }

    /// <summary>Open with a password and return the decrypted bytes.
    /// Throws if the password is wrong.</summary>
    public static byte[] Decrypt(byte[] pdf, string password)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), password, PdfDocumentOpenMode.Modify);
        doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    /// <summary>Return a copy encrypted with the given user password (128-bit).</summary>
    public static byte[] Encrypt(byte[] pdf, string password)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = password;
        doc.SecuritySettings.OwnerPassword = password;
        doc.SecurityHandler.SetEncryptionToV2With128Bits();
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }
}

/// <summary>Document metadata snapshot for the properties dialog.</summary>
public sealed record DocumentProperties(
    string Title, string Author, string Subject, string Keywords,
    string Creator, string Producer, string Created, string Modified,
    string Version, int PageCount, string PageSize, string FileSize)
{
    public static DocumentProperties Read(byte[] pdf, string? filePath)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var info = doc.Info;

        string pageSize = "—";
        if (doc.PageCount > 0)
        {
            var p = doc.Pages[0];
            double wMm = p.Width.Point * 25.4 / 72, hMm = p.Height.Point * 25.4 / 72;
            pageSize = $"{p.Width.Point:0.#} × {p.Height.Point:0.#} pt  ({wMm:0} × {hMm:0} mm)";
        }

        string size = pdf.LongLength switch
        {
            < 1024 => $"{pdf.LongLength} B",
            < 1024 * 1024 => $"{pdf.LongLength / 1024.0:0.#} KB",
            _ => $"{pdf.LongLength / (1024.0 * 1024.0):0.##} MB",
        };

        static string Date(DateTime dt) => dt.Year > 1900 ? dt.ToString("g") : "—";
        static string Str(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

        return new DocumentProperties(
            Str(info.Title), Str(info.Author), Str(info.Subject), Str(info.Keywords),
            Str(info.Creator), Str(info.Producer),
            Date(info.CreationDate), Date(info.ModificationDate),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PDF {doc.Version / 10.0:0.0}"),
            doc.PageCount, pageSize, size);
    }
}

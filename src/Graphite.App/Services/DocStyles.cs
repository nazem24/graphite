using System.Windows;
using System.Windows.Documents;

namespace Graphite.App.Services;

/// <summary>Unit helpers: Word counts in points and twips, WPF in device-independent pixels.</summary>
public static class Units
{
    public const double DipPerPt = 96.0 / 72.0;
    public static double PtToDip(double pt) => pt * DipPerPt;
    public static double DipToPt(double dip) => dip / DipPerPt;
    public static int DipToTwips(double dip) => (int)Math.Round(dip * 15.0);   // dip -> pt (x0.75) -> twips (x20)
    public static double TwipsToDip(double twips) => twips / 15.0;
    public static long DipToEmu(double dip) => (long)Math.Round(dip * 9525.0);
    public static double EmuToDip(double emu) => emu / 9525.0;
}

/// <summary>Paper size and margins of a document (points), written to / read from the .docx section.</summary>
public sealed class PageSettings
{
    public double WidthPt { get; set; } = 595.3;   // A4
    public double HeightPt { get; set; } = 841.9;
    public double MarginPt { get; set; } = 72;     // 1 inch

    public bool IsLetter => Math.Abs(WidthPt - 612) < 1;
    public string SizeName => IsLetter ? "Letter" : "A4";
    public string MarginName => MarginPt switch { <= 40 => "Narrow", >= 100 => "Wide", _ => "Normal" };
    public double WidthDip => Units.PtToDip(WidthPt);
    public double HeightDip => Units.PtToDip(HeightPt);
    public double MarginDip => Units.PtToDip(MarginPt);
    public double TextWidthDip => Units.PtToDip(WidthPt - 2 * MarginPt);
    public double TextWidthPt => WidthPt - 2 * MarginPt;

    public PageSettings Clone() => new() { WidthPt = WidthPt, HeightPt = HeightPt, MarginPt = MarginPt };

    public void SetSize(string name)
    {
        if (name == "Letter") { WidthPt = 612; HeightPt = 792; }
        else { WidthPt = 595.3; HeightPt = 841.9; }
    }

    public void SetMargins(string name) => MarginPt = name switch { "Narrow" => 36, "Wide" => 108, _ => 72 };
}

/// <summary>
/// The paragraph styles the editor offers. A paragraph remembers its style id in
/// <see cref="FrameworkContentElement.Tag"/>; the same ids are the style ids in the saved .docx.
/// </summary>
public static class DocStyles
{
    public const string Normal = "Normal";
    public const string Title = "Title";
    public const string Heading1 = "Heading1";
    public const string Heading2 = "Heading2";
    public const string Heading3 = "Heading3";

    public const string DefaultFont = "Segoe UI";
    public const double DefaultSizePt = 11;

    /// <summary>What the style drop-down lists, in order.</summary>
    public static readonly string[] All = { Normal, Title, Heading1, Heading2, Heading3 };

    public static string DisplayName(string id) => id switch
    {
        Title => "Title",
        Heading1 => "Heading 1",
        Heading2 => "Heading 2",
        Heading3 => "Heading 3",
        _ => "Normal text",
    };

    public static string Normalize(string? id) => id switch
    {
        Title => Title,
        Heading1 or "heading1" or "Heading 1" => Heading1,
        Heading2 or "heading2" or "Heading 2" => Heading2,
        Heading3 or "heading3" or "Heading 3" => Heading3,
        _ => Normal,
    };

    public static double SizePt(string id) => id switch
    {
        Title => 26,
        Heading1 => 20,
        Heading2 => 16,
        Heading3 => 13,
        _ => DefaultSizePt,
    };

    public static bool IsBold(string id) => id != Normal;

    public static double SpaceBeforePt(string id) => id switch
    {
        Heading1 => 18,
        Heading2 => 14,
        Heading3 => 10,
        _ => 0,
    };

    public static double SpaceAfterPt(string id) => id switch
    {
        Title => 8,
        Heading1 => 6,
        Heading2 or Heading3 => 4,
        _ => 8,
    };

    /// <summary>-1 for body text; 0 for the title, 1..3 for headings.</summary>
    public static int OutlineLevel(string id) => id switch
    {
        Title => 0,
        Heading1 => 1,
        Heading2 => 2,
        Heading3 => 3,
        _ => -1,
    };

    public static string Of(Paragraph p) => (p.Tag is string id && id.Length > 0) ? Normalize(id) : Normal;

    /// <summary>Give <paramref name="p"/> the look of a style (size, weight, spacing).</summary>
    public static void Apply(Paragraph p, string id)
    {
        id = Normalize(id);
        p.Tag = id;
        p.FontSize = Units.PtToDip(SizePt(id));
        p.FontWeight = IsBold(id) ? FontWeights.Bold : FontWeights.Normal;
        p.Margin = new Thickness(0, Units.PtToDip(SpaceBeforePt(id)), 0, Units.PtToDip(SpaceAfterPt(id)));
    }

    public static Paragraph NewParagraph(string id = Normal)
    {
        var p = new Paragraph();
        Apply(p, id);
        return p;
    }
}

/// <summary>An image placed in a document: the original file bytes travel with the picture so
/// saving does not have to re-encode it.</summary>
public sealed class EmbeddedImage
{
    public EmbeddedImage(byte[] bytes, string extension)
    {
        Bytes = bytes;
        Extension = extension;
    }

    public byte[] Bytes { get; }
    public string Extension { get; }
}

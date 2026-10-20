using System.Text.RegularExpressions;

namespace Graphite.Core.Canvas;

/// <summary>
/// The short mark a course card shows behind its title ("ACA", "IA", "MAR"). Canvas does report a course code, but at
/// many schools it is a number ("3,135,1.00") that says nothing at a glance, so the mark is the code only when the
/// code is itself a short word; otherwise it is worked out from the course's name.
/// </summary>
public static class CourseText
{
    private const int MaxLength = 4;

    // Little words that carry no meaning in an abbreviation (English, German, French).
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "at", "for", "in", "of", "on", "or", "the", "to", "with",
        "und", "oder", "der", "die", "das", "des", "dem", "fur", "für", "von", "zu", "zur", "zum", "im", "in", "am", "mit",
        "et", "de", "du", "la", "le", "les", "pour", "en",
    };

    private static readonly Regex Separators = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex RomanNumeral = new(@"^[IVX]{1,4}$", RegexOptions.Compiled);

    /// <summary>The mark for a course: its code when that is a short word, else the initials of the main words of its
    /// name ("Accounting, Controlling, Auditing" → ACA, "International Affairs: Theory and Practice" → IA), or the
    /// first three letters of a one-word name ("Macroeconomics" → MAC). Never longer than four characters, never empty
    /// unless the name has no letters at all.</summary>
    public static string Abbreviation(string? name, string? code)
    {
        string c = (code ?? "").Trim();
        if (c.Length is >= 2 and <= MaxLength && c.All(char.IsLetter))
            return c.ToUpperInvariant();

        string text = (name ?? "").Trim();

        // The main part of the name: what comes before a subtitle, a bracket or a dash.
        int cut = text.IndexOfAny(new[] { ':', '(', '[', '–', '—', '/' });
        int spacedDash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (spacedDash >= 0 && (cut < 0 || spacedDash < cut)) cut = spacedDash;
        string main = cut > 0 ? text[..cut] : text;

        var words = Words(main);
        if (words.Count == 0) words = Words(text);   // "(Seminar)" and the like: use everything
        if (words.Count == 0) return "";

        if (words.Count == 1)
        {
            string w = words[0];
            return w[..Math.Min(3, w.Length)].ToUpperInvariant();
        }

        return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
    }

    /// <summary>How big the mark can be drawn so every length fits the same space: short marks are large.</summary>
    public static double AbbreviationSize(string abbreviation) => abbreviation.Length switch
    {
        <= 2 => 54,
        3 => 46,
        _ => 36,
    };

    private static List<string> Words(string text) =>
        Separators.Split(text)
            .Where(w => w.Length > 0
                        && !Filler.Contains(w)
                        && !w.All(char.IsDigit)
                        && !RomanNumeral.IsMatch(w))
            .ToList();
}

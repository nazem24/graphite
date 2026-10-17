using System.Globalization;
using System.Text;

namespace Graphite.Core.Fonts;

/// <summary>
/// Just enough of the PDF standard-14 Helvetica family to lay out and write text in
/// annotation and form-field appearance streams without embedding a font: glyph widths
/// (Adobe AFM metrics, WinAnsi code order) and WinAnsi encoding. Every PDF viewer has these
/// fonts built in, which is also what the /DA strings Graphite writes refer to.
/// </summary>
public static class StandardFonts
{
    /// <summary>Helvetica / Helvetica-Oblique advance widths for WinAnsi codes 32..255 (1/1000 em).</summary>
    private static readonly short[] Regular =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 761,
        556, 0, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0,
        0, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 0, 500, 667,
        278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333,
        400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611,
        667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
        556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500,
    };

    /// <summary>Helvetica-Bold / Helvetica-BoldOblique advance widths for WinAnsi codes 32..255.</summary>
    private static readonly short[] Bold =
    {
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584, 761,
        556, 0, 278, 556, 500, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0,
        0, 278, 278, 500, 500, 350, 556, 1000, 333, 1000, 556, 333, 944, 0, 500, 667,
        278, 333, 556, 556, 556, 556, 280, 556, 333, 737, 370, 556, 584, 333, 737, 333,
        400, 584, 333, 333, 333, 611, 556, 278, 333, 333, 365, 556, 834, 834, 834, 611,
        722, 722, 722, 722, 722, 722, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
        556, 556, 556, 556, 556, 556, 889, 556, 556, 556, 556, 556, 278, 278, 278, 278,
        611, 611, 611, 611, 611, 611, 611, 584, 611, 611, 611, 611, 611, 556, 611, 556,
    };

    /// <summary>Unicode → WinAnsi for the 0x80..0x9F block (everything else in 0x20..0xFF maps 1:1).</summary>
    private static readonly Dictionary<char, byte> Cp1252High = new()
    {
        ['\u20AC'] = 0x80, ['\u201A'] = 0x82, ['\u0192'] = 0x83, ['\u201E'] = 0x84, ['\u2026'] = 0x85,
        ['\u2020'] = 0x86, ['\u2021'] = 0x87, ['\u02C6'] = 0x88, ['\u2030'] = 0x89, ['\u0160'] = 0x8A,
        ['\u2039'] = 0x8B, ['\u0152'] = 0x8C, ['\u017D'] = 0x8E, ['\u2018'] = 0x91, ['\u2019'] = 0x92,
        ['\u201C'] = 0x93, ['\u201D'] = 0x94, ['\u2022'] = 0x95, ['\u2013'] = 0x96, ['\u2014'] = 0x97,
        ['\u02DC'] = 0x98, ['\u2122'] = 0x99, ['\u0161'] = 0x9A, ['\u203A'] = 0x9B, ['\u0153'] = 0x9C,
        ['\u017E'] = 0x9E, ['\u0178'] = 0x9F,
    };

    /// <summary>The WinAnsi byte for a character; '?' for anything the encoding can't represent.</summary>
    public static byte Encode(char c)
    {
        if (c is >= ' ' and <= '~') return (byte)c;
        if (c is >= '\u00A0' and <= '\u00FF') return (byte)c;
        if (Cp1252High.TryGetValue(c, out byte b)) return b;
        if (c == '\t') return (byte)' ';
        return (byte)'?';
    }

    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++) bytes[i] = Encode(text[i]);
        return bytes;
    }

    /// <summary>Advance width of one character in text space units (1/1000 em).</summary>
    public static int CharWidth(char c, bool bold)
    {
        int code = Encode(c);
        if (code < 32) return 0;
        int w = (bold ? Bold : Regular)[code - 32];
        return w > 0 ? w : 556;
    }

    /// <summary>Width of a string in points at the given font size.</summary>
    public static double Width(string text, double size, bool bold = false)
    {
        long total = 0;
        foreach (char c in text) total += CharWidth(c, bold);
        return total * size / 1000.0;
    }

    /// <summary>A PDF literal string — "(...)" — holding the WinAnsi bytes of <paramref name="text"/>.
    /// Non-ASCII bytes are written as octal escapes so the content stream stays plain ASCII.</summary>
    public static string Literal(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('(');
        foreach (byte b in Encode(text))
        {
            switch (b)
            {
                case (byte)'(': sb.Append("\\("); break;
                case (byte)')': sb.Append("\\)"); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                default:
                    if (b < 32 || b > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    else sb.Append((char)b);
                    break;
            }
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>Greedy word wrap to <paramref name="maxWidth"/> points; explicit line breaks are kept.
    /// A single word wider than the line is broken by characters.</summary>
    public static List<string> Wrap(string text, double size, double maxWidth, bool bold = false)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(""); continue; }
            var line = new StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (Width(candidate, size, bold) <= maxWidth || line.Length == 0 && Width(word, size, bold) <= maxWidth)
                {
                    line.Clear().Append(candidate);
                    continue;
                }
                if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                // The word alone may still not fit: break it by characters.
                string rest = word;
                while (rest.Length > 0 && Width(rest, size, bold) > maxWidth)
                {
                    int n = 1;
                    while (n < rest.Length && Width(rest[..(n + 1)], size, bold) <= maxWidth) n++;
                    lines.Add(rest[..n]);
                    rest = rest[n..];
                }
                line.Append(rest);
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    /// <summary>Number formatting for content streams (invariant, no exponent, ≤ 3 decimals).</summary>
    public static string F(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
        string s = Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}

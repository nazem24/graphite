using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Graphite.Core.Annotations;
using Graphite.Core.Fonts;
using Graphite.Core.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Graphite.Core.Forms;

/// <summary>
/// Reads the fields of an interactive (AcroForm) PDF and writes filled-in values back.
///
/// Filling sets each field's value (/V), switches check boxes and radio buttons (/AS) and writes
/// a fresh appearance stream for text and choice fields, so the value shows the same in every
/// viewer — including the ones that never rebuild appearances themselves. Operations are pure:
/// bytes in, bytes out.
/// </summary>
public static class FormCodec
{
    // Field flags (PDF 32000-1, 12.7.3.1 / 12.7.4), bit positions are 1-based in the spec.
    private const int FlagReadOnly = 1 << 0;
    private const int FlagRequired = 1 << 1;
    private const int FlagMultiline = 1 << 12;
    private const int FlagPassword = 1 << 13;
    private const int FlagNoToggleToOff = 1 << 14;
    private const int FlagRadio = 1 << 15;
    private const int FlagPushButton = 1 << 16;
    private const int FlagCombo = 1 << 17;
    private const int FlagEdit = 1 << 18;
    private const int FlagComb = 1 << 24;

    // ---------------------------------------------------------------- read

    /// <summary>Every field with at least one widget on a page, in document order.</summary>
    public static List<FormField> Read(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return Walk(doc).Select(n => n.Field).ToList();
    }

    /// <summary>True when the document has an AcroForm with at least one field (cheap check).</summary>
    public static bool HasFields(byte[] pdf)
    {
        try
        {
            using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
            var acro = PdfValues.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
            return PdfValues.Array(acro?.Elements["/Fields"]) is { Elements.Count: > 0 };
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- write

    /// <summary>
    /// Set field values by fully qualified name. Text and choice fields take their text / export
    /// value; check boxes and radio buttons take the on-state name of the widget to switch on, or
    /// "Off". Unknown names and read-only fields are ignored.
    /// </summary>
    public static byte[] Fill(byte[] pdf, IReadOnlyDictionary<string, string> values)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        var nodes = Walk(doc);
        if (nodes.Count == 0) return pdf;

        var acroForm = PdfValues.Dict(doc.Internals.Catalog.Elements["/AcroForm"])!;
        PdfDictionary? fallbackFonts = null;
        PdfDictionary FallbackFonts() => fallbackFonts ??= AnnotationCodec.EnsureDefaultFontResources(doc);

        bool changed = false;
        foreach (var node in nodes)
        {
            if (!values.TryGetValue(node.Field.Name, out var value) || node.Field.ReadOnly) continue;
            value ??= "";
            changed = true;
            node.Field.Value = value;

            switch (node.Field.Kind)
            {
                case FormFieldKind.Text:
                    node.Dict.Elements["/V"] = new PdfString(value);
                    foreach (var w in node.Widgets)
                        SetAppearance(doc, w.Dict, BuildTextAppearance(doc, node, w, value, acroForm, FallbackFonts));
                    break;

                case FormFieldKind.CheckBox:
                case FormFieldKind.Radio:
                {
                    string state = FormField.IsOff(value) ? "Off" : value;
                    node.Dict.Elements["/V"] = new PdfName("/" + state);
                    foreach (var w in node.Widgets)
                    {
                        bool on = state != "Off" && w.Model.OnState == state;
                        EnsureToggleAppearance(doc, w.Dict, w.Model.OnState, node.Field.Kind == FormFieldKind.Radio);
                        w.Dict.Elements["/AS"] = new PdfName(on ? "/" + w.Model.OnState : "/Off");
                    }
                    break;
                }

                case FormFieldKind.ComboBox:
                case FormFieldKind.ListBox:
                {
                    node.Dict.Elements["/V"] = new PdfString(value);
                    int index = node.Field.Options.FindIndex(o => o.Export == value);
                    if (index >= 0)
                    {
                        var sel = new PdfArray(doc);
                        sel.Elements.Add(new PdfInteger(index));
                        node.Dict.Elements["/I"] = sel;
                    }
                    else node.Dict.Elements.Remove("/I");

                    string shown = node.Field.DisplayValue;
                    foreach (var w in node.Widgets)
                        SetAppearance(doc, w.Dict, node.Field.Kind == FormFieldKind.ComboBox
                            ? BuildTextAppearance(doc, node, w, shown, acroForm, FallbackFonts)
                            : BuildListAppearance(doc, node, w, index, acroForm, FallbackFonts));
                    break;
                }
            }
        }

        if (!changed) return pdf;
        // /NeedAppearances is deliberately left alone: with it set, viewers throw these
        // appearances away and redraw fields their own way — MuPDF shows a drop-down's export
        // code instead of its label, Poppler drops check marks it has no font for.

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- field tree

    private sealed record WidgetNode(PdfDictionary Dict, FormWidget Model, PageGeometry Geometry);

    private sealed record FieldNode(FormField Field, PdfDictionary Dict, List<WidgetNode> Widgets, string DefaultAppearance);

    private sealed record Inherited(string? FieldType, int Flags, PdfItem? Value, string DefaultAppearance, int Alignment,
        PdfArray? Options, int MaxLength);

    private static List<FieldNode> Walk(PdfDocument doc)
    {
        var result = new List<FieldNode>();
        var acroForm = PdfValues.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
        var fields = PdfValues.Array(acroForm?.Elements["/Fields"]);
        if (acroForm == null || fields == null || fields.Elements.Count == 0) return result;

        // Which page each widget sits on: /Annots is authoritative, /P is optional and sometimes wrong.
        var pageOf = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        var geometry = new PageGeometry[doc.PageCount];
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            geometry[p] = PageGeometry.Of(page);
            if (PdfValues.Array(page.Elements["/Annots"]) is not { } annots) continue;
            foreach (var item in annots.Elements)
                if (PdfValues.Dict(item) is { } a && PdfValues.Name(a.Elements["/Subtype"]) == "Widget")
                    pageOf.TryAdd(a, p);
        }

        var root = new Inherited(null, 0, null,
            PdfValues.Text(acroForm.Elements["/DA"]) ?? "/Helv 0 Tf 0 g",
            (int)(PdfValues.Number(acroForm.Elements["/Q"]) ?? 0), null, 0);
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in fields.Elements)
            if (PdfValues.Dict(item) is { } f)
                Visit(doc, f, "", root, visited, pageOf, geometry, result);
        return result;
    }

    private static void Visit(PdfDocument doc, PdfDictionary node, string parentName, Inherited inherited,
        HashSet<PdfDictionary> visited, Dictionary<PdfDictionary, int> pageOf, PageGeometry[] geometry, List<FieldNode> result)
    {
        if (!visited.Add(node) || visited.Count > 20000) return;

        string? partial = PdfValues.Text(node.Elements["/T"]);
        string name = partial == null ? parentName : parentName.Length == 0 ? partial : parentName + "." + partial;

        var inh = inherited with
        {
            FieldType = PdfValues.Name(node.Elements["/FT"]) ?? inherited.FieldType,
            Flags = node.Elements.ContainsKey("/Ff") ? (int)(PdfValues.Number(node.Elements["/Ff"]) ?? 0) : inherited.Flags,
            Value = node.Elements["/V"] ?? inherited.Value,
            DefaultAppearance = PdfValues.Text(node.Elements["/DA"]) ?? inherited.DefaultAppearance,
            Alignment = node.Elements.ContainsKey("/Q") ? (int)(PdfValues.Number(node.Elements["/Q"]) ?? 0) : inherited.Alignment,
            Options = PdfValues.Array(node.Elements["/Opt"]) ?? inherited.Options,
            MaxLength = node.Elements.ContainsKey("/MaxLen") ? (int)(PdfValues.Number(node.Elements["/MaxLen"]) ?? 0) : inherited.MaxLength,
        };

        var widgets = new List<PdfDictionary>();
        if (PdfValues.Array(node.Elements["/Kids"]) is { } kids)
        {
            foreach (var item in kids.Elements)
            {
                if (PdfValues.Dict(item) is not { } kid) continue;
                // A kid with a name (or kids of its own) is a child field; otherwise it is a widget.
                bool isField = kid.Elements.ContainsKey("/T") || kid.Elements.ContainsKey("/Kids") ||
                               PdfValues.Name(kid.Elements["/Subtype"]) != "Widget" && !kid.Elements.ContainsKey("/Rect");
                if (isField) Visit(doc, kid, name, inh, visited, pageOf, geometry, result);
                else widgets.Add(kid);
            }
        }
        else if (PdfValues.Name(node.Elements["/Subtype"]) == "Widget" || node.Elements.ContainsKey("/Rect"))
        {
            widgets.Add(node); // field and widget merged into one dictionary
        }

        if (widgets.Count == 0 || inh.FieldType == null || name.Length == 0) return;

        var kind = inh.FieldType switch
        {
            "Tx" => FormFieldKind.Text,
            "Btn" when (inh.Flags & FlagPushButton) != 0 => FormFieldKind.PushButton,
            "Btn" when (inh.Flags & FlagRadio) != 0 => FormFieldKind.Radio,
            "Btn" => FormFieldKind.CheckBox,
            "Ch" when (inh.Flags & FlagCombo) != 0 => FormFieldKind.ComboBox,
            "Ch" => FormFieldKind.ListBox,
            "Sig" => FormFieldKind.Signature,
            _ => (FormFieldKind?)null,
        };
        if (kind == null) return;

        var (fontName, fontSize, color) = ParseDefaultAppearance(inh.DefaultAppearance);
        _ = fontName;
        var field = new FormField
        {
            Name = name,
            Kind = kind.Value,
            Tooltip = PdfValues.Text(node.Elements["/TU"]) ?? "",
            ReadOnly = (inh.Flags & FlagReadOnly) != 0,
            Required = (inh.Flags & FlagRequired) != 0,
            Multiline = kind == FormFieldKind.Text && (inh.Flags & FlagMultiline) != 0,
            Password = kind == FormFieldKind.Text && (inh.Flags & FlagPassword) != 0,
            Comb = kind == FormFieldKind.Text && (inh.Flags & FlagComb) != 0,
            Editable = kind == FormFieldKind.ComboBox && (inh.Flags & FlagEdit) != 0,
            NoToggleToOff = (inh.Flags & FlagNoToggleToOff) != 0,
            MaxLength = Math.Max(0, inh.MaxLength),
            FontSize = fontSize,
            TextColorHex = color,
            Alignment = Math.Clamp(inh.Alignment, 0, 2),
        };

        if (inh.Options != null)
            foreach (var item in inh.Options.Elements)
            {
                if (PdfValues.Array(item) is { Elements.Count: >= 2 } pair)
                    field.Options.Add(new FormOption(PdfValues.Text(pair.Elements[0]) ?? "", PdfValues.Text(pair.Elements[1]) ?? ""));
                else if (PdfValues.Text(item) is { } single)
                    field.Options.Add(new FormOption(single, single));
            }

        var widgetNodes = new List<WidgetNode>();
        foreach (var w in widgets)
        {
            int page = PageOf(doc, w, pageOf);
            if (page < 0) continue;
            if (PdfValues.Box(w.Elements["/Rect"]) is not { } r) continue;
            var geo = geometry[page];
            var bounds = geo.ToDisplayRect(r.X1, r.Y1, r.X2, r.Y2);
            var model = new FormWidget
            {
                PageIndex = page,
                Bounds = bounds,
                OnState = kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? OnStateOf(w) : "Yes",
            };
            field.Widgets.Add(model);
            widgetNodes.Add(new WidgetNode(w, model, geo));
        }
        if (field.Widgets.Count == 0) return;

        field.Value = ReadValue(field, inh.Value, widgetNodes);
        result.Add(new FieldNode(field, node, widgetNodes, inh.DefaultAppearance));
    }

    private static int PageOf(PdfDocument doc, PdfDictionary widget, Dictionary<PdfDictionary, int> pageOf)
    {
        if (pageOf.TryGetValue(widget, out int p)) return p;
        if (PdfValues.Dict(widget.Elements["/P"]) is { } pageDict)
            for (int i = 0; i < doc.PageCount; i++)
                if (ReferenceEquals(doc.Pages[i], pageDict) ||
                    (pageDict.Reference != null && doc.Pages[i].Reference != null &&
                     pageDict.Reference.ObjectID == doc.Pages[i].Reference!.ObjectID))
                    return i;
        return -1;
    }

    /// <summary>The name a check box / radio widget uses for "on": its appearance-state key other than /Off.</summary>
    private static string OnStateOf(PdfDictionary widget)
    {
        var ap = PdfValues.Dict(widget.Elements["/AP"]);
        foreach (string key in new[] { "/N", "/D" })
            if (PdfValues.Dict(ap?.Elements[key]) is { } states)
                foreach (var kv in states.Elements)
                    if (kv.Key != "/Off") return kv.Key.TrimStart('/');
        return "Yes";
    }

    private static string ReadValue(FormField field, PdfItem? raw, List<WidgetNode> widgets)
    {
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox:
            case FormFieldKind.Radio:
            {
                string? v = PdfValues.Name(raw) ?? PdfValues.Text(raw);
                if (!string.IsNullOrEmpty(v)) return v;
                // No /V: fall back to whichever widget's appearance state is on.
                foreach (var w in widgets)
                    if (PdfValues.Name(w.Dict.Elements["/AS"]) is { } st && st != "Off") return st;
                return "Off";
            }
            case FormFieldKind.ComboBox:
            case FormFieldKind.ListBox:
                if (PdfValues.Array(raw) is { Elements.Count: > 0 } multi)
                    return PdfValues.Text(multi.Elements[0]) ?? "";
                return PdfValues.Text(raw) ?? "";
            default:
                return PdfValues.Text(raw) ?? "";
        }
    }

    // ---------------------------------------------------------------- default appearance

    private static readonly Regex DaFont = new(@"/([^\s/\[\]()<>]+)\s+(-?[\d.]+)\s+Tf", RegexOptions.Compiled);

    /// <summary>(font resource name, size, color) from a /DA string like "/Helv 10 Tf 0 0 1 rg".</summary>
    internal static (string Font, double Size, string Color) ParseDefaultAppearance(string? da)
    {
        string font = "Helv";
        double size = 0;
        string color = "#000000";
        if (string.IsNullOrEmpty(da)) return (font, size, color);

        var m = DaFont.Match(da);
        if (m.Success)
        {
            font = m.Groups[1].Value;
            double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out size);
            size = Math.Max(0, size);
        }

        // Last colour operator wins (g = gray, rg = RGB, k = CMYK).
        var tokens = da.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            double N(int at) => double.TryParse(tokens[at], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 1) : 0;
            if (tokens[i] == "rg" && i >= 3) { color = Annotation.ToHex(N(i - 3), N(i - 2), N(i - 1)); break; }
            if (tokens[i] == "g" && i >= 1) { double gray = N(i - 1); color = Annotation.ToHex(gray, gray, gray); break; }
            if (tokens[i] == "k" && i >= 4)
            {
                double c = N(i - 4), mm = N(i - 3), y = N(i - 2), k = N(i - 1);
                color = Annotation.ToHex((1 - c) * (1 - k), (1 - mm) * (1 - k), (1 - y) * (1 - k));
                break;
            }
        }
        return (font, size, color);
    }

    // ---------------------------------------------------------------- appearances

    private static void SetAppearance(PdfDocument doc, PdfDictionary widget, PdfDictionary form)
    {
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form;
        widget.Elements["/AP"] = ap;
        widget.Elements.Remove("/AS");
    }

    /// <summary>Widget box size in its own orientation, and its rotation (/MK /R).</summary>
    private static (double W, double H, int Rotation) WidgetBox(PdfDictionary widget)
    {
        double w = 0, h = 0;
        if (PdfValues.Box(widget.Elements["/Rect"]) is { } r)
        {
            w = r.X2 - r.X1;
            h = r.Y2 - r.Y1;
        }
        int rot = PageGeometry.Normalize((int)(PdfValues.Number(PdfValues.Dict(widget.Elements["/MK"])?.Elements["/R"]) ?? 0));
        return rot is 90 or 270 ? (h, w, rot) : (w, h, rot);
    }

    /// <summary>Background (/MK /BG) and border (/MK /BC, /BS /W) of a widget, as content-stream operators.</summary>
    private static (string Ops, double BorderWidth) Frame(PdfDictionary widget, double w, double h)
    {
        var sb = new StringBuilder();
        var mk = PdfValues.Dict(widget.Elements["/MK"]);
        double bw = PdfValues.Number(PdfValues.Dict(widget.Elements["/BS"])?.Elements["/W"]) ?? 1;
        if (ColorOp(PdfValues.Array(mk?.Elements["/BG"]), fill: true) is { } bg)
            sb.Append(bg).Append(" 0 0 ").Append(F(w)).Append(' ').Append(F(h)).Append(" re f\n");
        if (ColorOp(PdfValues.Array(mk?.Elements["/BC"]), fill: false) is { } bc && bw > 0)
            sb.Append(bc).Append(' ').Append(F(bw)).Append(" w ")
              .Append(F(bw / 2)).Append(' ').Append(F(bw / 2)).Append(' ')
              .Append(F(w - bw)).Append(' ').Append(F(h - bw)).Append(" re S\n");
        else bw = 0;
        return (sb.ToString(), bw);
    }

    private static string? ColorOp(PdfArray? arr, bool fill)
    {
        if (arr == null) return null;
        var c = arr.Elements.Select(e => PdfValues.Number(e) ?? 0).ToList();
        return c.Count switch
        {
            1 => $"{F(c[0])} {(fill ? "g" : "G")}",
            3 => $"{F(c[0])} {F(c[1])} {F(c[2])} {(fill ? "rg" : "RG")}",
            4 => $"{F(c[0])} {F(c[1])} {F(c[2])} {F(c[3])} {(fill ? "k" : "K")}",
            _ => null, // empty array = transparent
        };
    }

    /// <summary>The font to typeset a field with: the /DA font from the form's resources when it
    /// is a simple single-byte font we can write WinAnsi text for, else the standard Helvetica.</summary>
    private static (string Name, PdfDictionary Font, bool Bold) ResolveFont(PdfDictionary acroForm, string daFont,
        Func<PdfDictionary> fallbackFonts)
    {
        var dr = PdfValues.Dict(acroForm.Elements["/DR"]);
        var fonts = PdfValues.Dict(dr?.Elements["/Font"]);
        if (PdfValues.Dict(fonts?.Elements["/" + daFont]) is { } f)
        {
            string subtype = PdfValues.Name(f.Elements["/Subtype"]) ?? "";
            string? enc = PdfValues.Name(f.Elements["/Encoding"]);
            string baseFont = PdfValues.Name(f.Elements["/BaseFont"]) ?? "";
            bool simple = subtype is "Type1" or "TrueType" or "MMType1";
            // A simple font with no /Encoding or WinAnsi takes our bytes as intended; symbol fonts don't.
            bool winAnsi = enc is null or "WinAnsiEncoding" && !baseFont.Contains("Zapf", StringComparison.OrdinalIgnoreCase)
                           && !baseFont.Contains("Symbol", StringComparison.OrdinalIgnoreCase);
            if (simple && winAnsi)
                return ("/" + daFont, f, baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase));
        }
        var std = fallbackFonts();
        return ("/Helv", PdfValues.Dict(std.Elements["/Helv"])!, false);
    }

    private static PdfDictionary BuildTextAppearance(PdfDocument doc, FieldNode node, WidgetNode widget, string text,
        PdfDictionary acroForm, Func<PdfDictionary> fallbackFonts)
    {
        var field = node.Field;
        var (w, h, rotation) = WidgetBox(widget.Dict);
        string da = PdfValues.Text(widget.Dict.Elements["/DA"]) ?? node.DefaultAppearance;
        var (daFont, daSize, daColor) = ParseDefaultAppearance(da);
        var (fontName, font, bold) = ResolveFont(acroForm, daFont, fallbackFonts);
        var (frame, borderWidth) = Frame(widget.Dict, w, h);
        double pad = Math.Max(1, borderWidth) + 1;
        double innerW = Math.Max(1, w - 2 * pad), innerH = Math.Max(1, h - 2 * pad);

        if (field.Password) text = new string('*', text.Length);
        var lines = field.Multiline ? null : new List<string> { text.Replace("\r", " ").Replace("\n", " ") };

        // Font size: from /DA, or auto (0) — fit the height (single line) or the whole text (multiline).
        double size = daSize;
        if (size <= 0)
        {
            if (field.Multiline)
            {
                size = 12;
                while (size > 4 && StandardFonts.Wrap(text, size, innerW, bold).Count * size * 1.15 > innerH) size -= 0.5;
            }
            else
            {
                size = Math.Clamp(innerH / 1.15, 4, 12);
                if (!field.Comb)
                    while (size > 4 && StandardFonts.Width(lines![0], size, bold) > innerW) size -= 0.5;
            }
        }
        lines ??= StandardFonts.Wrap(text, size, innerW, bold);

        var (r, g, b) = Annotation.ParseColor(daColor);
        var sb = new StringBuilder();
        sb.Append(frame);
        sb.Append("/Tx BMC\nq\n");
        sb.Append(F(pad)).Append(' ').Append(F(pad)).Append(' ').Append(F(innerW)).Append(' ').Append(F(innerH)).Append(" re W n\n");
        sb.Append("BT\n").Append(fontName).Append(' ').Append(F(size)).Append(" Tf\n");
        sb.Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" rg\n");

        if (field.Comb && field.MaxLength > 0 && !field.Multiline)
        {
            double cell = w / field.MaxLength;
            double y = (h - size * 0.7) / 2;
            string s = lines[0];
            for (int i = 0; i < s.Length && i < field.MaxLength; i++)
            {
                string ch = s[i].ToString();
                double x = i * cell + (cell - StandardFonts.Width(ch, size, bold)) / 2;
                sb.Append("1 0 0 1 ").Append(F(x)).Append(' ').Append(F(y)).Append(" Tm ")
                  .Append(StandardFonts.Literal(ch)).Append(" Tj\n");
            }
        }
        else
        {
            double lineHeight = size * 1.15;
            double y = field.Multiline ? h - pad - size * 0.9 : (h - size * 0.7) / 2;
            foreach (string line in lines)
            {
                double tw = StandardFonts.Width(line, size, bold);
                double x = field.Alignment switch
                {
                    1 => (w - tw) / 2,
                    2 => w - pad - tw,
                    _ => pad,
                };
                sb.Append("1 0 0 1 ").Append(F(x)).Append(' ').Append(F(y)).Append(" Tm ")
                  .Append(StandardFonts.Literal(line)).Append(" Tj\n");
                y -= lineHeight;
                if (y < pad - size) break;
            }
        }
        sb.Append("ET\nQ\nEMC\n");

        var resources = new PdfDictionary(doc);
        var fontRes = new PdfDictionary(doc);
        fontRes.Elements[fontName] = font;
        resources.Elements["/Font"] = fontRes;
        return AppearanceBuilder.NewForm(doc, w, h, rotation, resources, Encoding.ASCII.GetBytes(sb.ToString()));
    }

    private static PdfDictionary BuildListAppearance(PdfDocument doc, FieldNode node, WidgetNode widget, int selected,
        PdfDictionary acroForm, Func<PdfDictionary> fallbackFonts)
    {
        var field = node.Field;
        var (w, h, rotation) = WidgetBox(widget.Dict);
        string da = PdfValues.Text(widget.Dict.Elements["/DA"]) ?? node.DefaultAppearance;
        var (daFont, daSize, daColor) = ParseDefaultAppearance(da);
        var (fontName, font, _) = ResolveFont(acroForm, daFont, fallbackFonts);
        var (frame, borderWidth) = Frame(widget.Dict, w, h);
        double pad = Math.Max(1, borderWidth) + 1;
        double size = daSize > 0 ? daSize : 12;
        double lineHeight = size * 1.15;
        var (r, g, b) = Annotation.ParseColor(daColor);

        // Scroll so the selected option is visible (like the /TI top index viewers use).
        int visible = Math.Max(1, (int)((h - 2 * pad) / lineHeight));
        int top = selected >= visible ? selected - visible + 1 : 0;

        var sb = new StringBuilder();
        sb.Append(frame);
        sb.Append("/Tx BMC\nq\n");
        sb.Append(F(pad)).Append(' ').Append(F(pad)).Append(' ').Append(F(w - 2 * pad)).Append(' ').Append(F(h - 2 * pad)).Append(" re W n\n");
        for (int i = top; i < field.Options.Count && i < top + visible + 1; i++)
        {
            double rowTop = h - pad - (i - top) * lineHeight;
            if (i == selected)
                sb.Append("0.6 0.75 0.95 rg ").Append(F(pad)).Append(' ').Append(F(rowTop - lineHeight)).Append(' ')
                  .Append(F(w - 2 * pad)).Append(' ').Append(F(lineHeight)).Append(" re f\n");
            sb.Append("BT ").Append(fontName).Append(' ').Append(F(size)).Append(" Tf ")
              .Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append(" rg 1 0 0 1 ")
              .Append(F(pad + 1)).Append(' ').Append(F(rowTop - size * 0.95)).Append(" Tm ")
              .Append(StandardFonts.Literal(field.Options[i].Display)).Append(" Tj ET\n");
        }
        sb.Append("Q\nEMC\n");

        var resources = new PdfDictionary(doc);
        var fontRes = new PdfDictionary(doc);
        fontRes.Elements[fontName] = font;
        resources.Elements["/Font"] = fontRes;
        return AppearanceBuilder.NewForm(doc, w, h, rotation, resources, Encoding.ASCII.GetBytes(sb.ToString()));
    }

    /// <summary>Check boxes and radio buttons normally carry their own on/off appearances; give one a
    /// simple check mark / dot when the file has none for its on-state, so switching it shows.</summary>
    private static void EnsureToggleAppearance(PdfDocument doc, PdfDictionary widget, string onState, bool radio)
    {
        var ap = PdfValues.Dict(widget.Elements["/AP"]);
        var normal = PdfValues.Dict(ap?.Elements["/N"]);
        if (normal != null && normal.Elements.ContainsKey("/" + onState)) return;

        var (w, h, rotation) = WidgetBox(widget);
        var (frame, _) = Frame(widget, w, h);
        double s = Math.Min(w, h);
        var on = new StringBuilder(frame);
        if (radio)
        {
            // Filled dot as four Béziers.
            double cx = w / 2, cy = h / 2, rr = s * 0.25, k = 0.5523 * rr;
            on.Append("0 g ")
              .Append(F(cx + rr)).Append(' ').Append(F(cy)).Append(" m ")
              .Append(F(cx + rr)).Append(' ').Append(F(cy + k)).Append(' ').Append(F(cx + k)).Append(' ').Append(F(cy + rr)).Append(' ').Append(F(cx)).Append(' ').Append(F(cy + rr)).Append(" c ")
              .Append(F(cx - k)).Append(' ').Append(F(cy + rr)).Append(' ').Append(F(cx - rr)).Append(' ').Append(F(cy + k)).Append(' ').Append(F(cx - rr)).Append(' ').Append(F(cy)).Append(" c ")
              .Append(F(cx - rr)).Append(' ').Append(F(cy - k)).Append(' ').Append(F(cx - k)).Append(' ').Append(F(cy - rr)).Append(' ').Append(F(cx)).Append(' ').Append(F(cy - rr)).Append(" c ")
              .Append(F(cx + k)).Append(' ').Append(F(cy - rr)).Append(' ').Append(F(cx + rr)).Append(' ').Append(F(cy - k)).Append(' ').Append(F(cx + rr)).Append(' ').Append(F(cy)).Append(" c f\n");
        }
        else
        {
            // Check mark stroke.
            double ox = (w - s) / 2, oy = (h - s) / 2;
            on.Append("0 G ").Append(F(Math.Max(1, s * 0.12))).Append(" w 1 J 1 j ")
              .Append(F(ox + s * 0.2)).Append(' ').Append(F(oy + s * 0.52)).Append(" m ")
              .Append(F(ox + s * 0.42)).Append(' ').Append(F(oy + s * 0.28)).Append(" l ")
              .Append(F(ox + s * 0.8)).Append(' ').Append(F(oy + s * 0.75)).Append(" l S\n");
        }

        var emptyRes = new PdfDictionary(doc);
        var onForm = AppearanceBuilder.NewForm(doc, w, h, rotation, emptyRes, Encoding.ASCII.GetBytes(on.ToString()));
        var offForm = AppearanceBuilder.NewForm(doc, w, h, rotation, new PdfDictionary(doc), Encoding.ASCII.GetBytes(frame));

        if (ap == null)
        {
            ap = new PdfDictionary(doc);
            widget.Elements["/AP"] = ap;
        }
        if (normal == null)
        {
            normal = new PdfDictionary(doc);
            ap.Elements["/N"] = normal;
        }
        normal.Elements["/" + onState] = onForm;
        if (!normal.Elements.ContainsKey("/Off")) normal.Elements["/Off"] = offForm;
    }

    private static string F(double v) => StandardFonts.F(v);
}

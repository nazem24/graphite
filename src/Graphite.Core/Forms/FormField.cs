namespace Graphite.Core.Forms;

public enum FormFieldKind
{
    Text,
    CheckBox,
    Radio,
    ComboBox,
    ListBox,
    PushButton,
    Signature,
}

/// <summary>One choice of a combo or list box: the value written to the file and the text shown.</summary>
public readonly record struct FormOption(string Export, string Display);

/// <summary>Where a field shows up: a widget annotation on a page.</summary>
public sealed class FormWidget
{
    public required int PageIndex { get; init; }

    /// <summary>Bounds in display space (points, top-left origin, page rotation applied).</summary>
    public required RectD Bounds { get; init; }

    /// <summary>Check boxes and radio buttons: the name this widget writes when it is switched on
    /// ("Yes", "Option2", …). Unused for other field types.</summary>
    public string OnState { get; init; } = "Yes";
}

/// <summary>
/// A fillable field of an interactive (AcroForm) PDF, flattened to what a filling UI needs.
/// <see cref="Name"/> is the fully qualified name ("address.street"), which is also the key
/// <see cref="FormCodec.Fill"/> takes.
/// </summary>
public sealed class FormField
{
    public required string Name { get; init; }
    public required FormFieldKind Kind { get; init; }

    /// <summary>Text for text fields; the export value for choices; for check boxes and radio
    /// buttons the on-state name of the selected widget, or "Off".</summary>
    public string Value { get; set; } = "";

    /// <summary>Tooltip text (/TU), shown as a hint; empty when the file has none.</summary>
    public string Tooltip { get; init; } = "";

    public List<FormOption> Options { get; } = new();
    public List<FormWidget> Widgets { get; } = new();

    public bool ReadOnly { get; init; }
    public bool Required { get; init; }
    public bool Multiline { get; init; }
    public bool Password { get; init; }
    public bool Comb { get; init; }

    /// <summary>Combo box that also accepts typed values.</summary>
    public bool Editable { get; init; }

    /// <summary>Radio group whose selected button can't be clicked off again.</summary>
    public bool NoToggleToOff { get; init; }

    /// <summary>Maximum characters for text fields (0 = unlimited).</summary>
    public int MaxLength { get; init; }

    /// <summary>Font size from the field's default appearance; 0 = auto-size.</summary>
    public double FontSize { get; init; }

    /// <summary>Text color from the default appearance (#RRGGBB).</summary>
    public string TextColorHex { get; init; } = "#000000";

    /// <summary>0 = left, 1 = centered, 2 = right.</summary>
    public int Alignment { get; init; }

    /// <summary>Whether the person can fill this field in Graphite.</summary>
    public bool IsFillable => !ReadOnly && Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature);

    public bool IsOn(FormWidget widget) => !IsOff(Value) && string.Equals(Value, widget.OnState, StringComparison.Ordinal);

    public static bool IsOff(string? value) => string.IsNullOrEmpty(value) || value == "Off";

    /// <summary>The text shown for the current value (a choice's display text, else the value).</summary>
    public string DisplayValue
    {
        get
        {
            foreach (var o in Options)
                if (o.Export == Value) return o.Display;
            return Value;
        }
    }
}

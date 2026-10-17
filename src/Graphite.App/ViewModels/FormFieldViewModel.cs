using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.Core.Forms;

namespace Graphite.App.ViewModels;

/// <summary>A fillable field of the open PDF form, as the page overlays see it.</summary>
public sealed partial class FormFieldViewModel : ObservableObject
{
    public DocumentViewModel Doc { get; }
    public FormField Model { get; }

    public FormFieldViewModel(DocumentViewModel doc, FormField model)
    {
        Doc = doc;
        Model = model;
    }

    public string Name => Model.Name;
    public FormFieldKind Kind => Model.Kind;
    public string Value => Model.Value;
    public string DisplayValue => Model.DisplayValue;

    /// <summary>Hover text: the form's own tooltip, else the field name.</summary>
    public string Hint => string.IsNullOrWhiteSpace(Model.Tooltip) ? Model.Name : Model.Tooltip;

    public bool IsOn(FormWidget widget) => Model.IsOn(widget);

    /// <summary>Write a new value into the document.</summary>
    public Task SetAsync(string value) => Doc.SetFormValueAsync(this, value);

    /// <summary>Check box / radio button clicked: the value the field should get.</summary>
    public string ToggledValue(FormWidget widget)
    {
        if (!IsOn(widget)) return widget.OnState;
        // Clicking the selected button of a radio group that can't be switched off does nothing.
        return Kind == FormFieldKind.Radio && Model.NoToggleToOff ? widget.OnState : "Off";
    }

    /// <summary>The document wrote <paramref name="value"/>: update the model and bindings.</summary>
    internal void ApplyValue(string value)
    {
        Model.Value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DisplayValue));
    }
}

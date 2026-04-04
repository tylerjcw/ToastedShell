namespace Tosh.Tui.Widgets;

/// <summary>Configuration for an option picker widget (single-select from typed choices).</summary>
public sealed class TuiOptionPickerConfig : ITuiWidget
{
    public TuiOptionPickerConfig(string id, IReadOnlyList<object?> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(options);

        Id = id;
        Options = options;
    }

    public string Id { get; }

    public TuiWidgetKind Kind => TuiWidgetKind.OptionPicker;

    /// <summary>Available options.</summary>
    public IReadOnlyList<object?> Options { get; }

    /// <summary>Property name used to produce the display label for each option.
    /// When null, <c>ToString()</c> is used.</summary>
    public string? DisplayProperty { get; set; }

    /// <summary>Prompt text shown above the picker.</summary>
    public string? Prompt { get; set; }
}

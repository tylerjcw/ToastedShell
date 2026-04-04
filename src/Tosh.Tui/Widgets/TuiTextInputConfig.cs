namespace Tosh.Tui.Widgets;

/// <summary>Configuration for a text input widget.</summary>
public sealed class TuiTextInputConfig : ITuiWidget
{
    public TuiTextInputConfig(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; }

    public TuiWidgetKind Kind => TuiWidgetKind.TextInput;

    /// <summary>Prompt text shown above or beside the input field.</summary>
    public string? Prompt { get; set; }

    /// <summary>Initial value pre-filled in the input field.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Allow multiple lines of input.</summary>
    public bool Multiline { get; set; }
}

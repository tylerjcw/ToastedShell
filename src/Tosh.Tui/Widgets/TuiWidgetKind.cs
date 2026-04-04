namespace Tosh.Tui.Widgets;

/// <summary>Identifies a widget type for layout and dispatch purposes.</summary>
public enum TuiWidgetKind
{
    /// <summary>Selectable item list with optional search.</summary>
    List,

    /// <summary>Read-only scrollable text or object display.</summary>
    Text,

    /// <summary>Single-line or multi-line text input field.</summary>
    TextInput,

    /// <summary>Directory and file browser.</summary>
    FilePicker,

    /// <summary>Pick one option from a list of choices.</summary>
    OptionPicker,

    /// <summary>Yes/No confirmation dialog.</summary>
    Confirmation,
}

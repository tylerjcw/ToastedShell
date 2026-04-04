namespace Tosh.Core;

/// <summary>
/// Provides inline (non-fullscreen) prompts that render within the current terminal output flow.
/// Implemented by the CLI host; not available in headless/test environments.
/// </summary>
public interface IInlinePromptProvider
{
    /// <summary>Shows an inline selection list. Returns the selected item(s) or null if cancelled.</summary>
    IReadOnlyList<object?>? Pick(IReadOnlyList<object?> items, string? prompt = null, string? displayProperty = null, bool multiSelect = false, int pageSize = 10);

    /// <summary>Shows an inline yes/no confirmation. Returns true for yes, false for no, null if cancelled.</summary>
    bool? Confirm(string message, bool defaultValue = true);

    /// <summary>Shows an inline text input prompt. Returns the entered text or null if cancelled.</summary>
    string? Input(string? prompt = null, string? defaultValue = null, bool password = false);

    /// <summary>Shows an inline searchable filter list. Returns selected item(s) or null if cancelled.</summary>
    IReadOnlyList<object?>? Filter(IReadOnlyList<object?> items, string? prompt = null, string? displayProperty = null, bool multiSelect = false, int pageSize = 10);
}

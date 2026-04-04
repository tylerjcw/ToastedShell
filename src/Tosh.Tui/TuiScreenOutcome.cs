namespace Tosh.Tui;

/// <summary>Result returned after a TUI screen closes.</summary>
public sealed class TuiScreenOutcome
{
    /// <summary>Objects selected by the user (e.g. picked list items).</summary>
    public IReadOnlyList<object?> Selected { get; init; } = Array.Empty<object?>();

    /// <summary>Whether the user cancelled the screen (Escape / Q).</summary>
    public bool Cancelled { get; init; }

    /// <summary>Widget values keyed by widget id. Useful for custom screens with
    /// multiple input widgets.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } =
        new Dictionary<string, object?>();
}

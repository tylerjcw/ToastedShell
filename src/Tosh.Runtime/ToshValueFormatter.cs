namespace Tosh.Runtime;

/// <summary>
/// Canonical default display formatting for runtime and emitted code paths
/// that do not carry a configurable <see cref="ToshRuntime"/> instance.
/// </summary>
public static class ToshValueFormatter
{
    private static readonly ObjectFormatter DefaultFormatter = new();

    /// <summary>Formats one value using ToastScript's default display profile.</summary>
    public static string Format(object? value) => DefaultFormatter.Format(value);
}

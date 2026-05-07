namespace Tosh.Language;

/// <summary>
/// Wraps a named argument value during function call evaluation.
/// </summary>
public sealed record NamedArgument(string Name, object? Value) : Tosh.Runtime.INamedArgument;

namespace Tosh.Core;

public sealed record ToshDiagnostic(
    string Code,
    string Title,
    string? SourceName = null,
    string? SourceText = null,
    TextSpan? Span = null,
    string? Label = null,
    string? Help = null,
    string? Info = null);

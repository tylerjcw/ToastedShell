using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public sealed record SyntaxDiagnostic(
    string Code,
    string Title,
    TextSpan Span,
    string? Label = null,
    string? Help = null);

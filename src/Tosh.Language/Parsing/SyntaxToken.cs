using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public sealed record SyntaxToken(SyntaxTokenKind Kind, int Position, string Text, object? Value = null)
{
    public TextSpan Span => new(Position, Text.Length);
}

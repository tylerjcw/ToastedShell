using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public sealed record SyntaxToken(SyntaxTokenKind Kind, int Position, string Text, object? Value = null)
{
    public TextSpan Span => new(Position, Text.Length);
}

/// <summary>
/// The resolved unit carried by a <see cref="SyntaxTokenKind.UnitSuffix"/> token.
/// Resolved in the lexer so an unknown unit is reported where it is written,
/// exactly as it is for the magnitude-and-unit literal form.
/// </summary>
public sealed record UnitSuffixInfo(Tosh.Runtime.Units.UnitExpression Dimension, string NormalizedSymbol);

using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Wraps an AST node as a first-class runtime value. Created by the
/// <c>quote { expr }</c> expression inside rune bodies. Allows macros
/// to inspect the syntactic structure of their arguments.
/// </summary>
public sealed class QuotedSyntax
{
    public QuotedSyntax(ArgumentSyntax syntax, string sourceName, string sourceText)
    {
        Syntax = syntax;
        SourceName = sourceName;
        SourceText = sourceText;
    }

    /// <summary>The captured AST node.</summary>
    public ArgumentSyntax Syntax { get; }

    /// <summary>Source file name.</summary>
    public string SourceName { get; }

    /// <summary>Full source text.</summary>
    public string SourceText { get; }

    /// <summary>
    /// Returns the original source text of the captured expression.
    /// </summary>
    public string ToSource()
    {
        var span = Syntax.Span;
        if (span.Start >= 0 && span.End <= SourceText.Length && span.End > span.Start)
        {
            return SourceText[span.Start..span.End].Trim();
        }

        return "<unknown>";
    }

    public override string ToString() => ToSource();
}

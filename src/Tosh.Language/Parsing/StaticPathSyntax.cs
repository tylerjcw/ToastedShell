namespace Tosh.Language.Parsing;

/// <summary>
/// The <c>::</c> path operator — <c>TOAST-0090</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>::</c> reaches into a <i>type</i>; <c>.</c> reaches into a <i>value</i>. Under one operator
/// nothing in the syntax says which is happening, so the reader resolves it from knowledge of the
/// types and so must every tool — and the parser, having no symbol table, resolves it by guessing
/// from capitalisation.
/// </para>
/// <para>
/// Both spellings resolve identically, so a path is canonicalised to dots the moment it is parsed
/// and every consumer downstream sees one form. Only the syntax node remembers which operator was
/// written, which is all the formatter, hover and the migration analysis need.
/// </para>
/// <para>
/// The lexer needs no change: <c>::</c> already stays inside a bareword, which is why
/// <c>System::Math::PI</c> reached the engine as an unknown <i>command</i> rather than a parse
/// error. Recognition is what was missing, not tokenisation.
/// </para>
/// </remarks>
internal static class StaticPathSyntax
{
    internal const string PathOperator = "::";

    /// <summary>
    /// Whether <paramref name="text"/> is written with the path operator.
    /// </summary>
    /// <remarks>
    /// A leading or trailing <c>::</c> is not a path — <c>::Foo</c> has no type to reach into and
    /// <c>Foo::</c> names nothing inside one — so both are declined here and left to fall through
    /// to whatever the text would otherwise have meant, rather than being accepted and then
    /// failing to resolve.
    /// </remarks>
    internal static bool UsesPathOperator(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !text.Contains(PathOperator, StringComparison.Ordinal))
        {
            return false;
        }

        if (text.StartsWith(PathOperator, StringComparison.Ordinal) ||
            text.EndsWith(PathOperator, StringComparison.Ordinal))
        {
            return false;
        }

        // Every `::` must separate two segments. `A:::B` splits to a segment that is bare `:`,
        // and `A::::B` to an empty one; neither is a name.
        foreach (var segment in text.Split(PathOperator, StringSplitOptions.None))
        {
            if (string.IsNullOrEmpty(segment) ||
                segment.StartsWith(':') ||
                segment.EndsWith(':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rewrites a path written with <c>::</c> into the canonical dotted spelling that resolution,
    /// lowering and the compiler all already understand. Text without the operator is returned
    /// unchanged.
    /// </summary>
    internal static string Canonicalize(string text) =>
        UsesPathOperator(text)
            ? text.Replace(PathOperator, ".", StringComparison.Ordinal)
            : text;
}

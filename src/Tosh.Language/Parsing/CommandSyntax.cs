using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public sealed record CommandSyntax(
    string Name,
    TextSpan NameSpan,
    IReadOnlyList<ArgumentSyntax> Arguments,
    TextSpan Span) : PipelineStageSyntax(Span)
{
    /// <summary>
    /// Reserved for a future binder phase that will stamp this with a
    /// resolved command reference as a runtime fast path. Phase 1 does not
    /// populate it: top-level user-function definitions register into the
    /// runtime command registry, which means a parse-time capture can go
    /// stale before evaluation. A later phase will add a freshness check
    /// and resume populating this annotation. Body-declared record
    /// properties are excluded from value equality, so this property does
    /// not affect AST equality semantics.
    /// </summary>
    public IShellCommand? BoundCommand { get; set; }

    /// <summary>
    /// Explicit generic type arguments written at the call site
    /// (e.g. <c>box&lt;int&gt; 42</c>). Adjacency is required: the
    /// <c>&lt;</c> must immediately follow the command name with no
    /// whitespace, so this never collides with input redirection
    /// (<c>&lt;(...)</c> is its own lexer token) or comparison.
    /// Body-declared record properties are excluded from value
    /// equality, so this property does not affect AST equality.
    /// </summary>
    public IReadOnlyList<string>? ExplicitTypeArguments { get; init; }
}

using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Represents an unevaluated rune argument. When a rune is invoked, its
/// arguments are not immediately evaluated — instead they are stored as
/// RuneThunk objects. When the rune body references a parameter, the
/// thunk is evaluated in the caller's scope on demand.
/// </summary>
internal sealed class RuneThunk
{
    public RuneThunk(
        ArgumentSyntax syntax,
        string sourceName,
        string sourceText,
        IReadOnlyList<LexicalScope>? callerScopes)
    {
        Syntax = syntax;
        SourceName = sourceName;
        SourceText = sourceText;
        CallerScopes = callerScopes;
    }

    /// <summary>The raw AST of the argument expression.</summary>
    public ArgumentSyntax Syntax { get; }

    /// <summary>Source file name for diagnostics.</summary>
    public string SourceName { get; }

    /// <summary>Full source text for diagnostics.</summary>
    public string SourceText { get; }

    /// <summary>Captured scope chain from the call site for hygienic evaluation.</summary>
    public IReadOnlyList<LexicalScope>? CallerScopes { get; }
}

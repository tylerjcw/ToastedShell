using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Runtime representation of a rune (macro) definition.
/// Runes capture their body as AST and expand it at the call site,
/// substituting parameters with unevaluated argument thunks.
/// </summary>
public sealed record RuneDefinition(
    string Name,
    IReadOnlyList<RuneParameterDefinition> Parameters,
    BlockSyntax Body,
    bool IsSealed,
    bool IsFixed,
    string SourceName,
    string SourceText,
    TextSpan Span,
    IReadOnlyList<LexicalScope>? CapturedScopes = null,
    DocComment? DocComment = null);

public sealed record RuneParameterDefinition(string Name, TextSpan Span);

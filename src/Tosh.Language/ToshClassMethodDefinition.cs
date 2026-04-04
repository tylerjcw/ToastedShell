using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record ToshClassMethodDefinition(
    string Name,
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    string? ReturnTypeName,
    BlockSyntax Body,
    bool IsStatic,
    bool IsShy,
    string SourceName,
    string SourceText,
    TextSpan Span,
    IReadOnlyList<LexicalScope>? CapturedScopes = null);

using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record FunctionDefinition(
    string Name,
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    string? ReturnTypeName,
    BlockSyntax Body,
    bool IsCommandWrapper,
    string SourceName,
    string SourceText,
    TextSpan Span,
    IReadOnlyList<LexicalScope>? CapturedScopes = null,
    DocComment? DocComment = null,
    bool IsGenerator = false,
    IReadOnlyList<string>? TypeParameters = null,
    string? RawReturnTypeName = null,
    IReadOnlyList<ToshTypeParameterConstraint>? TypeParameterConstraints = null);

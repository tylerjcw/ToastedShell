using Tosh.Core;
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
    DocComment? DocComment = null);

using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record FunctionDefinition(
    string Name,
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    string? ReturnTypeName,
    BlockSyntax Body,
    string SourceName,
    string SourceText,
    TextSpan Span);

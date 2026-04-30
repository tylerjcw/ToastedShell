using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record ToshClassConstructorDefinition(
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    BlockSyntax Body,
    string SourceName,
    string SourceText,
    TextSpan Span,
    IReadOnlyList<LexicalScope>? CapturedScopes = null);

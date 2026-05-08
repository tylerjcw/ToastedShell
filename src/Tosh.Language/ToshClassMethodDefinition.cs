using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record ToshClassMethodDefinition(
    string Name,
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    string? ReturnTypeName,
    BlockSyntax Body,
    bool IsStatic,
    bool IsShy,
    bool IsAbstract,
    bool IsOverride,
    bool IsGuarded,
    bool IsFading,
    bool IsLocal,
    bool IsRaw,
    string SourceName,
    string SourceText,
    TextSpan Span,
    IReadOnlyList<LexicalScope>? CapturedScopes = null,
    string? RawReturnTypeName = null);

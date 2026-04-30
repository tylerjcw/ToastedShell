using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record ToshClassPropertyDefinition(
    string Name,
    string? TypeName,
    PipelineSyntax? Initializer,
    BlockSyntax? GetterBody,
    BlockSyntax? SetterBody,
    bool IsShy,
    bool IsStatic,
    bool IsFixed,
    bool IsVital,
    bool IsGuarded,
    bool IsLazy,
    bool IsFading,
    bool IsLocal,
    bool IsAbstract,
    TextSpan Span,
    RefinementAnnotation? Refinement = null)
{
    public bool IsComputed => GetterBody is not null;

    public bool IsWritable => !IsFixed && (SetterBody is not null || GetterBody is null);
}

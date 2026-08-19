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
    RefinementAnnotation? Refinement = null,
    /// <summary>
    /// The property's `##` comment — `TS-P2-101`. The parse side always carried this on
    /// <c>ClassPropertyMemberSyntax</c>; it stopped here, so a documented property was
    /// discoverable in the editor and invisible to <c>help</c>.
    /// </summary>
    DocComment? Documentation = null)
{
    public bool IsComputed => GetterBody is not null;

    public bool IsWritable => !IsFixed && (SetterBody is not null || GetterBody is null);
}

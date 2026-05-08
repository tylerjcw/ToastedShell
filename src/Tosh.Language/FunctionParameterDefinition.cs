using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record FunctionParameterDefinition(
    string Name,
    string? TypeName,
    bool IsOptional,
    bool IsRest,
    PipelineSyntax? DefaultValue,
    TextSpan Span,
    RefinementAnnotation? Refinement = null,
    string? RawTypeName = null);

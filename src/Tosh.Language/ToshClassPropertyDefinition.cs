using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record ToshClassPropertyDefinition(
    string Name,
    string? TypeName,
    PipelineSyntax? Initializer,
    BlockSyntax? GetterBody,
    BlockSyntax? SetterBody,
    bool IsShy,
    TextSpan Span)
{
    public bool IsComputed => GetterBody is not null;

    public bool IsWritable => SetterBody is not null || GetterBody is null;
}

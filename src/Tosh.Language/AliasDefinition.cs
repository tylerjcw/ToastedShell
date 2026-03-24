using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record AliasDefinition(
    string Name,
    PipelineSyntax Pipeline,
    string ExpansionText,
    string SourceName,
    string SourceText,
    TextSpan Span);

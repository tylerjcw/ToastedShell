namespace Tosh.Language.Parsing;

public sealed record ParseResult(
    string SourceName,
    string SourceText,
    StatementSyntax Statement,
    IReadOnlyList<SyntaxDiagnostic> Diagnostics,
    IReadOnlyList<LineHushDirective>? LineHushDirectives = null,
    IReadOnlyList<LineComment>? LineComments = null)
{
    public PipelineSyntax Pipeline => Statement is PipelineStatementSyntax pipeline
        ? pipeline.Pipeline
        : new PipelineSyntax(Array.Empty<PipelineStageSyntax>());
}

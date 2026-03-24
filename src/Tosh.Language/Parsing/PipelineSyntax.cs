namespace Tosh.Language.Parsing;

public sealed record PipelineSyntax(IReadOnlyList<PipelineStageSyntax> Stages)
{
    public IReadOnlyList<CommandSyntax> Commands => Stages.OfType<CommandSyntax>().ToArray();
}

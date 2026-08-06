namespace Tosh.Runtime;

public sealed record HelpTopic(
    string Name,
    HelpSubjectKind Kind,
    string Category,
    string Description,
    string Usage,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Related,
    IReadOnlyList<string> Examples,
    string? Path,
    string? Notes,
    IReadOnlyList<HelpArgumentInfo>? Arguments = null,
    IReadOnlyList<HelpOptionInfo>? Options = null,
    HelpPipelineInputInfo? PipelineInput = null,
    string? Output = null,
    IReadOnlyList<HelpExample>? ExampleItems = null,
    string? Streaming = null,
    /// <summary>
    /// Child commands, when this topic describes something that dispatches to them — a script
    /// built from <c>subcommand</c> blocks. Name and description only; the other
    /// <see cref="HelpArgumentInfo"/> fields do not apply.
    /// </summary>
    IReadOnlyList<HelpArgumentInfo>? Subcommands = null);

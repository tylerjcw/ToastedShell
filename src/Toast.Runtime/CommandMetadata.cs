namespace Tosh.Runtime;

// `TOAST-0006`. The records describing a command travel with the language; the exporter
// that writes them to disk stays with the shell, in CommandMetadataExporter.cs.

public sealed record CommandMetadata(
    string Name,
    string Description,
    string? LongDescription,
    string Usage,
    string Category,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<CommandArgumentMetadata> Arguments,
    IReadOnlyList<CommandOptionMetadata> Options,
    IReadOnlyList<CommandExampleMetadata> Examples,
    IReadOnlyList<string> Notes,
    string? Output,
    CommandPipelineInputMetadata? PipelineInput,
    string? OutputType,
    string? OutputMembers,
    string OutputMode,
    CommandSideEffectsMetadata? SideEffects,
    string? SinceVersion,
    string? DeprecatedVersion,
    string? RemovedVersion,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> SeeAlso,
    IReadOnlyList<string> Permissions,
    bool IsExperimental,
    IReadOnlyList<string> ErrorConditions,
    IReadOnlyList<CommandCanonicalExampleMetadata> CanonicalExamples,
    string? Stdlib = null,
    bool IsShellOnly = false,
    string? ShellOnlyReason = null,
    TypedTypeRef? OutputTypeInfo = null,
    string? Streaming = null);

public sealed record CommandCanonicalExampleMetadata(
    string Input,
    string Output,
    string? Description);

public sealed record CommandArgumentMetadata(
    string Name,
    string Description,
    bool Required,
    string? TypeName,
    string? Kind,
    TypedTypeRef? TypeInfo = null);

public sealed record CommandOptionMetadata(
    string Syntax,
    string Description,
    TypedTypeRef? ValueTypeInfo = null,
    bool IsFlag = false,
    string? Default = null);

public sealed record CommandExampleMetadata(
    string Code,
    string? Title);

public sealed record CommandPipelineInputMetadata(
    bool AcceptsScalar,
    bool AcceptsRecord,
    bool AcceptsList,
    bool AcceptsTable,
    string? Description);

public sealed record CommandSideEffectsMetadata(
    bool ReadsFiles,
    bool WritesFiles,
    bool Network,
    bool SpawnsProcess);

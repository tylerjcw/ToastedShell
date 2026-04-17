using System.Reflection;

namespace Tosh.Core;

public abstract class ShellCommand : IShellCommand
{
    protected ShellCommand(string name, string description, string usage)
    {
        Name = name;
        Description = description;
        Usage = usage;
    }

    public string Name { get; }

    public string Description { get; }

    public string Usage { get; }

    public abstract IAsyncEnumerable<object?> ExecuteAsync(CommandContext context);

    /// <summary>
    /// Builds canonical metadata for this command from its constructor values
    /// and attribute annotations. Subclasses may override to supply custom metadata.
    /// </summary>
    public virtual CommandMetadata GetMetadata(IReadOnlyList<string>? aliases = null)
    {
        var type = GetType();

        var category = type.GetCustomAttribute<CommandCategoryAttribute>()?.Category
                       ?? "Shell";

        var arguments = type.GetCustomAttributes<CommandArgumentAttribute>()
            .Select(a => new CommandArgumentMetadata(a.Name, a.Description, a.Required, a.TypeName, a.Kind ?? InferArgumentKind(a.TypeName)))
            .ToList();

        var options = type.GetCustomAttributes<CommandOptionAttribute>()
            .Select(o => new CommandOptionMetadata(o.Syntax, o.Description))
            .ToList();

        var examples = type.GetCustomAttributes<CommandExampleAttribute>()
            .Select(e => new CommandExampleMetadata(e.Code, e.Title))
            .ToList();

        var notes = type.GetCustomAttributes<CommandNoteAttribute>()
            .Select(n => n.Text)
            .ToList();

        var outputAttr = type.GetCustomAttribute<CommandOutputAttribute>();
        var output = outputAttr?.Description;

        CommandPipelineInputMetadata? pipelineInput = null;
        var pipeAttr = type.GetCustomAttribute<PipelineInputAttribute>();
        if (pipeAttr is not null)
            pipelineInput = new(pipeAttr.AcceptsScalar, pipeAttr.AcceptsRecord, pipeAttr.AcceptsList, pipeAttr.AcceptsTable, pipeAttr.Description);

        CommandSideEffectsMetadata? sideEffects = null;
        var seAttr = type.GetCustomAttribute<CommandSideEffectsAttribute>();
        if (seAttr is not null)
            sideEffects = new(seAttr.ReadsFiles, seAttr.WritesFiles, seAttr.Network, seAttr.SpawnsProcess);

        // New metadata fields extraction
        var longDescription = type.GetCustomAttribute<CommandLongDescriptionAttribute>()?.Text;
        var sinceVersion = type.GetCustomAttribute<CommandSinceAttribute>()?.Version;
        var deprecatedVersion = type.GetCustomAttribute<CommandDeprecatedAttribute>()?.Version;
        var removedVersion = type.GetCustomAttribute<CommandRemovedAttribute>()?.Version;
        var tags = type.GetCustomAttributes<CommandTagAttribute>().Select(t => t.Tag).ToList();
        var seeAlso = type.GetCustomAttributes<CommandSeeAlsoAttribute>().Select(s => s.RelatedCommand).ToList();
        var permissions = type.GetCustomAttributes<CommandPermissionAttribute>().Select(p => p.Permission).ToList();
        var isExperimental = type.GetCustomAttribute<CommandExperimentalAttribute>() is not null;
        var errorConditions = type.GetCustomAttributes<CommandErrorConditionAttribute>().Select(e => e.Description).ToList();
        var canonicalExamples = type.GetCustomAttributes<CommandCanonicalExampleAttribute>()
            .Select(c => new CommandCanonicalExampleMetadata(c.Input, c.Output, c.Description)).ToList();

        return new CommandMetadata(
            Name: Name,
            Description: Description,
            LongDescription: longDescription,
            Usage: Usage,
            Category: category,
            Aliases: aliases ?? [],
            Arguments: arguments,
            Options: options,
            Examples: examples,
            Notes: notes,
            Output: output,
            PipelineInput: pipelineInput,
            OutputType: outputAttr?.TypeName,
            OutputMembers: outputAttr?.Members,
            OutputMode: outputAttr?.Mode ?? "structured",
            SideEffects: sideEffects,
            SinceVersion: sinceVersion,
            DeprecatedVersion: deprecatedVersion,
            RemovedVersion: removedVersion,
            Tags: tags,
            SeeAlso: seeAlso,
            Permissions: permissions,
            IsExperimental: isExperimental,
            ErrorConditions: errorConditions,
            CanonicalExamples: canonicalExamples
        );
    }

    private static string? InferArgumentKind(string? typeName) => typeName?.ToLowerInvariant() switch
    {
        null => null,
        "path-like" or "path" => "path",
        "block|callable" or "block" or "callable" => "block",
        "string" => "string",
        var t when t.Contains("expression") => "expression",
        _ => "any"
    };
}

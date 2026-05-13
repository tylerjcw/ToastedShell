using System.Reflection;

namespace Tosh.Runtime;

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
            .Select(a => new CommandArgumentMetadata(
                a.Name,
                a.Description,
                a.Required,
                a.TypeName,
                a.Kind ?? InferArgumentKind(a.TypeName),
                BuildArgumentTypeInfo(a)))
            .ToList();

        var options = type.GetCustomAttributes<CommandOptionAttribute>()
            .Select(o => new CommandOptionMetadata(
                o.Syntax,
                o.Description,
                o.ValueClrType is null ? null : TypedTypeRefBuilder.FromType(o.ValueClrType),
                o.IsFlag,
                o.Default))
            .ToList();

        var examples = type.GetCustomAttributes<CommandExampleAttribute>()
            .Select(e => new CommandExampleMetadata(e.Code, e.Title))
            .ToList();

        var notes = type.GetCustomAttributes<CommandNoteAttribute>()
            .Select(n => n.Text)
            .ToList();

        var outputAttr = type.GetCustomAttribute<CommandOutputAttribute>();
        var output = outputAttr?.Description;
        var outputTypeInfo = outputAttr?.ClrType is { } outClr
            ? TypedTypeRefBuilder.FromType(outClr)
            : null;

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

        var stdlibCategory = StdlibCategoryResolver.Resolve(type);
        var shellOnlyAttr = type.GetCustomAttribute<ShellOnlyAttribute>();
        var streamingAttr = type.GetCustomAttribute<CommandStreamingAttribute>();

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
            CanonicalExamples: canonicalExamples,
            Stdlib: stdlibCategory?.ToString(),
            IsShellOnly: shellOnlyAttr is not null,
            ShellOnlyReason: shellOnlyAttr?.Reason,
            OutputTypeInfo: outputTypeInfo,
            Streaming: streamingAttr is null ? null : FormatStreamingBehavior(streamingAttr.Behavior)
        );
    }

    private static string FormatStreamingBehavior(StreamingBehavior behavior) => behavior switch
    {
        StreamingBehavior.Lazy => "lazy",
        StreamingBehavior.Eager => "eager",
        StreamingBehavior.ShortCircuit => "short-circuit",
        _ => behavior.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Build a <see cref="TypedTypeRef"/> for an argument from its
    /// declared <see cref="CommandArgumentAttribute.ClrType"/> when
    /// present, otherwise from the syntactic <c>Kind</c> string. The
    /// kind path lets attribute authors keep the existing
    /// <c>TypeName = "path"</c> / <c>"block"</c> shorthand while still
    /// receiving structured metadata.
    /// </summary>
    private static TypedTypeRef? BuildArgumentTypeInfo(CommandArgumentAttribute a)
    {
        if (a.ClrType is { } t) return TypedTypeRefBuilder.FromType(t, a.Refinement);

        var kind = (a.Kind ?? InferArgumentKind(a.TypeName))?.ToLowerInvariant();
        if (kind is null) return a.Refinement is null
            ? null
            : new TypedTypeRef(null, null, TypedTypeKind.Any, null, a.Refinement, true);

        var typedKind = kind switch
        {
            "path" => TypedTypeKind.Path,
            "block" or "callable" => TypedTypeKind.Block,
            "string" => TypedTypeKind.Scalar,
            "expression" => TypedTypeKind.Expression,
            "any" => TypedTypeKind.Any,
            _ => TypedTypeKind.Any,
        };
        // No backing CLR type \u2014 leave names null but record kind +
        // refinement so consumers can still type-check on shape.
        return new TypedTypeRef(
            ClrTypeName: null,
            AssemblyQualifiedName: null,
            Kind: typedKind,
            ElementType: null,
            Refinement: a.Refinement,
            IsNullable: !a.Required);
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

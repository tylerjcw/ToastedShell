using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tosh.Runtime;

/// <summary>
/// Builds canonical command metadata by merging attribute-based annotations
/// (preferred) with HelpCatalog fallback data for unannotated commands.
/// </summary>
public static class CommandMetadataExporter
{
    public static IReadOnlyList<CommandMetadata> BuildMetadata(ShellCommandRegistry registry)
    {
        // Build alias map: merge registry aliases (RegisterAlias) + heuristic grouping +
        // explicit [CommandAlias] declarations. Registry takes precedence.
        var registryAliasMap = registry.GetAliasMap();
        var heuristicAliasMap = HelpCatalog.BuildBuiltInAliasMap(registry.All, registryAliasMap);
        var explicitAliasMap = BuildExplicitAliasMap(registry.All);
        var aliasMap = MergeAliasMaps(heuristicAliasMap, explicitAliasMap);

        // Names that are canonical commands in registry aliases — these are always primary, never
        // re-canonicalized via GetPrimaryName length heuristic.
        var registryCanonicals = new HashSet<string>(registryAliasMap.Keys, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<CommandMetadata>();

        foreach (var command in registry.All)
        {
            // Skip commands explicitly marked as aliases — they appear in the primary command's Aliases list.
            var aliasAttr = command.GetType().GetCustomAttribute<CommandAliasAttribute>();
            if (aliasAttr is not null)
                continue;

            // Skip heuristic aliases that aren't the primary name.
            // Commands with explicit aliases (attribute or RegisterAlias) pointing to them are always primary.
            if (!explicitAliasMap.ContainsKey(command.Name) &&
                !registryCanonicals.Contains(command.Name) &&
                aliasMap.TryGetValue(command.Name, out var aliases) && aliases.Count > 0)
            {
                var primaryName = GetPrimaryName(command.Name, aliases);
                if (!string.Equals(primaryName, command.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (!seen.Add(command.Name))
                continue;

            var resolvedAliases = aliasMap.TryGetValue(command.Name, out var aliasList)
                ? aliasList.ToList()
                : new List<string>();

            entries.Add(command is ShellCommand shellCommand
                ? shellCommand.GetMetadata(resolvedAliases)
                : BuildEntryForNonShellCommand(command, resolvedAliases));
        }

        return entries.OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    public static string ExportMetadataJson(ShellCommandRegistry registry)
    {
        var metadata = BuildMetadata(registry);
        return JsonSerializer.Serialize(metadata, MetadataJsonContext.Default.IReadOnlyListCommandMetadata);
    }

    /// <summary>
    /// Builds an alias map from explicit <see cref="CommandAliasAttribute"/> annotations.
    /// Keys are canonical command names, values are lists of alias names pointing to them.
    /// </summary>
    private static Dictionary<string, List<string>> BuildExplicitAliasMap(IEnumerable<IShellCommand> commands)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            var aliasAttr = command.GetType().GetCustomAttribute<CommandAliasAttribute>();
            if (aliasAttr is null) continue;

            if (!map.TryGetValue(aliasAttr.CanonicalName, out var list))
            {
                list = [];
                map[aliasAttr.CanonicalName] = list;
            }
            list.Add(command.Name);
        }
        return map;
    }

    /// <summary>
    /// Merges heuristic and explicit alias maps. Explicit declarations take precedence.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> MergeAliasMaps(
        IReadOnlyDictionary<string, IReadOnlyList<string>> heuristic,
        Dictionary<string, List<string>> explicit_)
    {
        var merged = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in heuristic)
            merged[key] = value;

        // Explicit aliases override heuristic grouping for commands that declared them.
        foreach (var (canonical, aliases) in explicit_)
        {
            if (merged.TryGetValue(canonical, out var existing))
            {
                var combined = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                foreach (var a in aliases) combined.Add(a);
                merged[canonical] = combined.ToList();
            }
            else
            {
                merged[canonical] = aliases;
            }
        }

        return merged;
    }

    /// <summary>
    /// Fallback metadata builder for <see cref="IShellCommand"/> instances that are not
    /// <see cref="ShellCommand"/> subclasses (e.g. external process wrappers).
    /// </summary>
    private static CommandMetadata BuildEntryForNonShellCommand(
        IShellCommand command,
        List<string> aliases)
    {
        var category = "Shell";

        return new CommandMetadata(
            Name: command.Name,
            Description: command.Description,
            LongDescription: null,
            Usage: command.Usage,
            Category: category,
            Aliases: aliases,
            Arguments: [],
            Options: [],
            Examples: [],
            Notes: [],
            Output: null,
            PipelineInput: null,
            OutputType: null,
            OutputMembers: null,
            OutputMode: "structured",
            SideEffects: null,
            SinceVersion: null,
            DeprecatedVersion: null,
            RemovedVersion: null,
            Tags: [],
            SeeAlso: [],
            Permissions: [],
            IsExperimental: false,
            ErrorConditions: [],
            CanonicalExamples: [],
            OutputTypeInfo: null);
    }

    /// <summary>Pick the shortest name as the "primary" for an alias group.</summary>
    private static string GetPrimaryName(string name, IReadOnlyList<string> aliases)
    {
        var all = new List<string>(aliases) { name };
        return all.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).First();
    }
}

// ── Metadata model ────────────────────────────────────────────────────

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
    TypedTypeRef? OutputTypeInfo = null);

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
    bool IsFlag = false);

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

// ── JSON serialization context (AOT-safe) ─────────────────────────────

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<CommandMetadata>))]
internal partial class MetadataJsonContext : JsonSerializerContext;

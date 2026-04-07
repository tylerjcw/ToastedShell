using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tosh.Core;

/// <summary>
/// Exports a unified command manifest by merging attribute-based metadata
/// (preferred) with HelpCatalog fallback data for unannotated commands.
/// </summary>
public static class CommandManifestExporter
{
    public static IReadOnlyList<CommandManifestEntry> BuildManifest(ShellCommandRegistry registry)
    {
        var aliasMap = HelpCatalog.BuildBuiltInAliasMap(registry.All);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<CommandManifestEntry>();

        foreach (var command in registry.All)
        {
            // Skip aliases — they'll appear in the primary command's Aliases list.
            if (aliasMap.TryGetValue(command.Name, out var aliases) && aliases.Count > 0)
            {
                var primaryName = GetPrimaryName(command.Name, aliases);

                if (!string.Equals(primaryName, command.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!seen.Add(command.Name))
            {
                continue;
            }

            entries.Add(BuildEntry(command, aliasMap));
        }

        return entries.OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    public static string ExportJson(ShellCommandRegistry registry)
    {
        var manifest = BuildManifest(registry);
        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.IReadOnlyListCommandManifestEntry);
    }

    private static CommandManifestEntry BuildEntry(
        IShellCommand command,
        IReadOnlyDictionary<string, IReadOnlyList<string>> aliasMap)
    {
        var type = command.GetType();

        // --- Category: attribute > HelpCatalog fallback ---
        var category = type.GetCustomAttribute<CommandCategoryAttribute>()?.Category
                       ?? HelpCatalog.DetermineCommandCategory(command.Name, HelpSubjectKind.BuiltIn);

        // --- Arguments: attributes > CommandDetailsByName fallback ---
        var argAttrs = type.GetCustomAttributes<CommandArgumentAttribute>().ToArray();
        List<CommandManifestArgument> arguments;

        if (argAttrs.Length > 0)
        {
            arguments = argAttrs.Select(a => new CommandManifestArgument(a.Name, a.Description, a.Required, a.TypeName)).ToList();
        }
        else if (HelpCatalog.CommandDetailsByName.TryGetValue(command.Name, out var details) && details.Arguments is { Count: > 0 })
        {
            arguments = details.Arguments.Select(a => new CommandManifestArgument(a.Name, a.Description, a.Required, a.TypeName)).ToList();
        }
        else
        {
            arguments = [];
        }

        // --- Options: attributes > CommandDetailsByName fallback ---
        var optAttrs = type.GetCustomAttributes<CommandOptionAttribute>().ToArray();
        List<CommandManifestOption> options;

        if (optAttrs.Length > 0)
        {
            options = optAttrs.Select(o => new CommandManifestOption(o.Syntax, o.Description)).ToList();
        }
        else if (HelpCatalog.CommandDetailsByName.TryGetValue(command.Name, out var details2) && details2.Options is { Count: > 0 })
        {
            options = details2.Options.Select(o => new CommandManifestOption(o.Syntax, o.Description)).ToList();
        }
        else
        {
            options = [];
        }

        // --- Examples: attributes > CommandDetailsByName.Examples > ExamplesByName fallback ---
        var exAttrs = type.GetCustomAttributes<CommandExampleAttribute>().ToArray();
        List<CommandManifestExample> examples;

        if (exAttrs.Length > 0)
        {
            examples = exAttrs.Select(e => new CommandManifestExample(e.Code, e.Title)).ToList();
        }
        else if (HelpCatalog.CommandDetailsByName.TryGetValue(command.Name, out var details3) && details3.Examples is { Count: > 0 })
        {
            examples = details3.Examples.Select(e => new CommandManifestExample(e.Code, e.Title)).ToList();
        }
        else if (HelpCatalog.ExamplesByName.TryGetValue(command.Name, out var simpleExamples))
        {
            examples = simpleExamples.Select(e => new CommandManifestExample(e, null)).ToList();
        }
        else
        {
            examples = [];
        }

        // --- Notes: attributes > GetCommandNotes fallback ---
        var noteAttrs = type.GetCustomAttributes<CommandNoteAttribute>().ToArray();
        List<string> notes;

        if (noteAttrs.Length > 0)
        {
            notes = noteAttrs.Select(n => n.Text).ToList();
        }
        else
        {
            var catalogNote = HelpCatalog.GetCommandNotes(command.Name);
            notes = catalogNote is not null ? [catalogNote] : [];
        }

        // --- Output: attribute > CommandDetailsByName fallback ---
        var output = type.GetCustomAttribute<CommandOutputAttribute>()?.Description;

        if (output is null && HelpCatalog.CommandDetailsByName.TryGetValue(command.Name, out var details4))
        {
            output = details4.Output;
        }

        // --- Pipeline input: attribute > CommandDetailsByName fallback ---
        CommandManifestPipelineInput? pipelineInput = null;
        var pipeAttr = type.GetCustomAttribute<PipelineInputAttribute>();

        if (pipeAttr is not null)
        {
            pipelineInput = new(pipeAttr.AcceptsScalar, pipeAttr.AcceptsRecord, pipeAttr.AcceptsList, pipeAttr.AcceptsTable, pipeAttr.Description);
        }
        else if (HelpCatalog.CommandDetailsByName.TryGetValue(command.Name, out var details5) && details5.PipelineInput is { } pi)
        {
            pipelineInput = new(pi.Scalar, pi.Object, pi.PathLike, pi.Collection, pi.Notes);
        }

        // --- Aliases ---
        var aliases = aliasMap.TryGetValue(command.Name, out var aliasList)
            ? aliasList.ToList()
            : new List<string>();

        return new CommandManifestEntry(
            Name: command.Name,
            Description: command.Description,
            Usage: command.Usage,
            Category: category,
            Aliases: aliases,
            Arguments: arguments,
            Options: options,
            Examples: examples,
            Notes: notes,
            Output: output,
            PipelineInput: pipelineInput);
    }

    /// <summary>Pick the shortest name as the "primary" for an alias group.</summary>
    private static string GetPrimaryName(string name, IReadOnlyList<string> aliases)
    {
        var all = new List<string>(aliases) { name };
        return all.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).First();
    }
}

// ── Manifest model ────────────────────────────────────────────────────

public sealed record CommandManifestEntry(
    string Name,
    string Description,
    string Usage,
    string Category,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<CommandManifestArgument> Arguments,
    IReadOnlyList<CommandManifestOption> Options,
    IReadOnlyList<CommandManifestExample> Examples,
    IReadOnlyList<string> Notes,
    string? Output,
    CommandManifestPipelineInput? PipelineInput);

public sealed record CommandManifestArgument(
    string Name,
    string Description,
    bool Required,
    string? TypeName);

public sealed record CommandManifestOption(
    string Syntax,
    string Description);

public sealed record CommandManifestExample(
    string Code,
    string? Title);

public sealed record CommandManifestPipelineInput(
    bool AcceptsScalar,
    bool AcceptsRecord,
    bool AcceptsList,
    bool AcceptsTable,
    string? Description);

// ── JSON serialization context (AOT-safe) ─────────────────────────────

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<CommandManifestEntry>))]
internal partial class ManifestJsonContext : JsonSerializerContext;

using System.Text.RegularExpressions;

using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

[CommandCategory("Data")]
[CommandArgument("pattern|regex", "The .NET regular expression pattern or Regex object to apply.", TypeName = "string|regex")]
[CommandArgument("text ...", "Optional explicit text values. When omitted, reads pipeline text.", Required = false)]
[CommandOption("-a, --all", "Emit every match in each input value instead of only the first match.")]
[CommandOption("-i, --ignore-case", "Use case-insensitive matching.")]
[CommandOption("-m, --multiline", "Enable multiline mode so ^ and $ match line boundaries.")]
[CommandOption("-s, --singleline", "Enable singleline mode so . matches newlines.")]
[CommandOption("-x, --ignore-pattern-whitespace", "Ignore unescaped whitespace and allow # comments in the regex pattern.")]
[CommandOption("--explicit-capture", "Only capture explicitly named or numbered groups.")]
[CommandExample("ping -c 3 localhost | parse \"time=(?<time_ms>[0-9.]+) ms\"")]
[CommandExample("echo \"PID=42\" | parse \"PID=(?<Pid>[0-9]+)\"")]
[CommandExample("echo \"first\\nsecond\" | parse -am \"^(?<Value>\\\\w+)$\" | get Value")]
[CommandNote("Parse and match use .NET regular expressions, including named groups and inline modifiers like `(?im)`.")]
[CommandOutput("Structured records produced by the chosen parser (json/csv/yaml/etc.) — usually dictionaries, lists, or scalars.")]
public sealed class ParseCommand : ShellCommand
{
    private static readonly Regex TypedGroupSyntax = new(
        @"\(\?(?:<(?<name>[a-zA-Z_]\w*)\s*:\s*(?<type>[a-zA-Z_][\w.?\[\]]*)\s*>|'(?<name>[a-zA-Z_]\w*)\s*:\s*(?<type>[a-zA-Z_][\w.?\[\]]*)\s*')",
        RegexOptions.Compiled);

    public ParseCommand()
        : base("parse", "Parses text input with a regular expression into shell record objects.", "parse [-a] [-i] [-m] [-s] [-x] [--explicit-capture] <pattern|regex> [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            throw new InvalidOperationException("parse requires a regular expression pattern.");
        }

        var explicitInput = CommandArguments.Slice(parsed.Positionals, 1);
        var inputItems = await StructuredTextInput.ReadItemsAsync(
            context,
            explicitInput,
            "parse expects pipeline text or explicit text values after the regular expression.");

        var rawPatternArg = parsed.Positionals[0];
        Dictionary<string, string> groupTypes = new(StringComparer.Ordinal);
        object? effectivePatternArg = rawPatternArg;

        if (rawPatternArg is string patternString && TypedGroupSyntax.IsMatch(patternString))
        {
            foreach (Match m in TypedGroupSyntax.Matches(patternString))
            {
                var groupName = m.Groups["name"].Value;
                var typeName = m.Groups["type"].Value;
                groupTypes[groupName] = typeName;
            }

            effectivePatternArg = TypedGroupSyntax.Replace(patternString, match =>
            {
                var groupName = match.Groups["name"].Value;
                return match.Value.StartsWith("(?'", StringComparison.Ordinal)
                    ? $"(?'{groupName}'"
                    : $"(?<{groupName}>";
            });
        }

        var regex = ShellRegexUtilities.RequireRegex(context, parsed, effectivePatternArg, "regex", timeout: TimeSpan.FromSeconds(2));

        var emitAllMatches = parsed.HasFlag("a", "all");
        var namedGroupNames = regex.GetGroupNames()
            .Where(name => name != "0" && !int.TryParse(name, out _))
            .ToArray();

        foreach (var input in inputItems)
        {
            if (emitAllMatches)
            {
                var match = regex.Match(input);

                while (match.Success)
                {
                    yield return CreateProjection(context, match, namedGroupNames, groupTypes);
                    match = match.NextMatch();
                }
            }
            else
            {
                var match = regex.Match(input);

                if (match.Success)
                {
                    yield return CreateProjection(context, match, namedGroupNames, groupTypes);
                }
            }
        }
    }

    private static System.Dynamic.ExpandoObject CreateProjection(
        CommandContext context,
        Match match,
        IReadOnlyList<string> namedGroupNames,
        IReadOnlyDictionary<string, string> groupTypes)
    {
        if (namedGroupNames.Count > 0)
        {
            var pairs = new List<KeyValuePair<string, object?>>(namedGroupNames.Count);
            foreach (var name in namedGroupNames)
            {
                var group = match.Groups[name];
                object? value;

                if (group.Success)
                {
                    var rawText = group.Value;
                    if (groupTypes.TryGetValue(name, out var typeName))
                    {
                        value = CoerceValue(context, rawText, typeName, name);
                    }
                    else
                    {
                        value = rawText;
                    }
                }
                else
                {
                    value = null;
                }

                pairs.Add(new KeyValuePair<string, object?>(name, value));
            }

            return ShellRecordUtilities.CreateExpando(pairs);
        }

        if (match.Groups.Count > 1)
        {
            return ShellRecordUtilities.CreateExpando(
                Enumerable.Range(1, match.Groups.Count - 1)
                    .Select(index => new KeyValuePair<string, object?>($"Group{index}", match.Groups[index].Success ? match.Groups[index].Value : null)));
        }

        return ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Value", match.Value)]);
    }

    private static object? CoerceValue(CommandContext context, string rawText, string typeName, string groupName)
    {
        try
        {
            var isNullable = typeName.EndsWith('?');
            var baseTypeName = isNullable ? typeName[..^1] : typeName;

            return OperatorEvaluator.CastAs(
                rawText,
                baseTypeName,
                name =>
                {
                    if (context.LanguageRuntime.Classes.TryGetValue(name, out var rawDesc) &&
                        rawDesc is IShellTypeDescriptor userDesc)
                    {
                        return userDesc;
                    }

                    if (context.ShellTypes is { } view && view.TryGetNamedType(name, out var namedType))
                    {
                        return namedType;
                    }

                    var clrType = context.TypeResolver.Resolve(name);
                    if (clrType is not null)
                    {
                        return clrType;
                    }

                    if (ReflectionMetadataUtilities.TryResolveShellType(context, name, out var declared))
                    {
                        return declared;
                    }

                    return null;
                });
        }
        catch (Exception ex)
        {
            throw context.CreateDiagnostic(
                "tosh.runtime.parse_type_conversion_failed",
                $"Group '{groupName}' captured '{rawText}', which could not be converted to '{typeName}': {ex.Message}",
                argumentIndex: 0,
                label: $"cannot convert '{rawText}' to '{typeName}'");
        }
    }
}

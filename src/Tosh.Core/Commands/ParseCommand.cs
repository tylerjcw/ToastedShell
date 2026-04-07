using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

[CommandCategory("Data")]
public sealed class ParseCommand : ShellCommand
{
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
        var regex = ShellRegexUtilities.RequireRegex(context, parsed, parsed.Positionals[0], "regex", timeout: TimeSpan.FromSeconds(2));

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
                    yield return CreateProjection(match, namedGroupNames);
                    match = match.NextMatch();
                }
            }
            else
            {
                var match = regex.Match(input);

                if (match.Success)
                {
                    yield return CreateProjection(match, namedGroupNames);
                }
            }
        }
    }

    private static System.Dynamic.ExpandoObject CreateProjection(Match match, IReadOnlyList<string> namedGroupNames)
    {
        if (namedGroupNames.Count > 0)
        {
            return ShellRecordUtilities.CreateExpando(
                namedGroupNames
                    .Select(name => new KeyValuePair<string, object?>(name, match.Groups[name].Success ? match.Groups[name].Value : null)));
        }

        if (match.Groups.Count > 1)
        {
            return ShellRecordUtilities.CreateExpando(
                Enumerable.Range(1, match.Groups.Count - 1)
                    .Select(index => new KeyValuePair<string, object?>($"Group{index}", match.Groups[index].Success ? match.Groups[index].Value : null)));
        }

        return ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("Value", match.Value)]);
    }

}

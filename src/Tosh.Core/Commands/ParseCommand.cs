using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

public sealed class ParseCommand : ShellCommand
{
    public ParseCommand()
        : base("parse", "Parses text input with a regular expression into projected objects.", "parse [-a] [-i] <regex> [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            throw new InvalidOperationException("parse requires a regular expression pattern.");
        }

        var pattern = CommandArguments.RequireString(parsed.Positionals, 0, "regex");
        var explicitInput = CommandArguments.Slice(parsed.Positionals, 1);
        var inputItems = await StructuredTextInput.ReadItemsAsync(
            context,
            explicitInput,
            "parse expects pipeline text or explicit text values after the regular expression.");

        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

        if (parsed.HasFlag("i", "ignore-case"))
        {
            options |= RegexOptions.IgnoreCase;
        }

        Regex regex;

        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException exception)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::invalid_regex",
                title: $"The regular expression is invalid. {exception.Message}",
                argumentIndex: 0,
                label: "this regex could not be compiled");
        }

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

    private static ProjectedObject CreateProjection(Match match, IReadOnlyList<string> namedGroupNames)
    {
        if (namedGroupNames.Count > 0)
        {
            return new ProjectedObject(
                namedGroupNames
                    .Select(name => new ProjectedField(name, name, match.Groups[name].Success ? match.Groups[name].Value : null))
                    .ToArray());
        }

        if (match.Groups.Count > 1)
        {
            return new ProjectedObject(
                Enumerable.Range(1, match.Groups.Count - 1)
                    .Select(index => new ProjectedField($"Group{index}", $"Group{index}", match.Groups[index].Success ? match.Groups[index].Value : null))
                    .ToArray());
        }

        return new ProjectedObject([new ProjectedField("Value", "Value", match.Value)]);
    }

}

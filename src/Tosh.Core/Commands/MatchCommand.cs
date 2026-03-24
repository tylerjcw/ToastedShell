using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

public sealed class MatchCommand : ShellCommand
{
    public MatchCommand()
        : base("match", "Matches text with a regular expression and returns structured match objects.", "match [-i] <pattern> [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            throw new InvalidOperationException("match requires a regular expression pattern.");
        }

        var pattern = CommandArguments.RequireString(parsed.Positionals, 0, "pattern");
        var inputs = parsed.Positionals.Count > 1
            ? parsed.Positionals.Skip(1).Select(value => new TextInputLine(value?.ToString() ?? string.Empty, null, 1)).ToArray()
            : await TextInputUtilities.ReadLinesFromInputAsync(context, "match expects text from the pipeline or explicit text arguments.");
        var regex = new Regex(pattern, RegexOptions.Compiled | (parsed.HasFlag("i") ? RegexOptions.IgnoreCase : RegexOptions.None));
        var namedGroups = regex.GetGroupNames().Where(name => !int.TryParse(name, out _)).ToArray();

        foreach (var input in inputs)
        {
            foreach (Match match in regex.Matches(input.Text))
            {
                if (!match.Success)
                {
                    continue;
                }

                var fields = new List<ProjectedField>
                {
                    new("Value", "Value", match.Value),
                    new("Index", "Index", match.Index),
                    new("LineNumber", "LineNumber", input.LineNumber),
                };

                fields.AddRange(namedGroups.Select(name => new ProjectedField(name, name, match.Groups[name].Success ? match.Groups[name].Value : null)));
                yield return new ProjectedObject(fields);
            }
        }
    }
}

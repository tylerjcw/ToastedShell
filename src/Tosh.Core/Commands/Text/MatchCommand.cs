using System.Text.RegularExpressions;

namespace Tosh.Core.Commands.Text;

[Stdlib(StdlibCategory.Text)]
[CommandCategory("Text")]
[CommandArgument("pattern|regex", "The .NET regular expression pattern or Regex object to apply.", TypeName = "string|regex")]
[CommandArgument("text ...", "Optional explicit text values. When omitted, reads pipeline text.", Required = false)]
[CommandOption("-i, --ignore-case", "Use case-insensitive matching.")]
[CommandOption("-m, --multiline", "Enable multiline mode so ^ and $ match line boundaries.")]
[CommandOption("-s, --singleline", "Enable singleline mode so . matches newlines.")]
[CommandOption("-x, --ignore-pattern-whitespace", "Ignore unescaped whitespace and allow # comments in the regex pattern.")]
[CommandOption("--explicit-capture", "Only capture explicitly named or numbered groups.")]
[CommandExample("echo \"PID=42\" | match \"PID=(?<Pid>[0-9]+)\" | get Pid")]
[CommandExample("echo \"Alpha\" | match -i \"^alpha$\"")]
[CommandOutput("Match records describing each capture: full match, group values, and 0-based positions.")]
public sealed class MatchCommand : ShellCommand
{
    public MatchCommand()
        : base("match", "Matches text with a regular expression and returns shell record objects.", "match [-i] [-m] [-s] [-x] [--explicit-capture] <pattern|regex> [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            throw new InvalidOperationException("match requires a regular expression pattern.");
        }

        var regex = ShellRegexUtilities.RequireRegex(context, parsed, parsed.Positionals[0], "pattern", timeout: TimeSpan.FromSeconds(5));
        var inputs = parsed.Positionals.Count > 1
            ? parsed.Positionals.Skip(1).Select(value => new TextInputLine(value?.ToString() ?? string.Empty, null, 1)).ToArray()
            : await TextInputUtilities.ReadLinesFromInputAsync(context, "match expects text from the pipeline or explicit text arguments.");
        var namedGroups = regex.GetGroupNames().Where(name => !int.TryParse(name, out _)).ToArray();

        foreach (var input in inputs)
        {
            foreach (Match match in regex.Matches(input.Text))
            {
                if (!match.Success)
                {
                    continue;
                }

                var fields = new List<KeyValuePair<string, object?>>
                {
                    new("Value", match.Value),
                    new("Index", match.Index),
                    new("LineNumber", input.LineNumber),
                };

                fields.AddRange(namedGroups.Select(name => new KeyValuePair<string, object?>(name, match.Groups[name].Success ? match.Groups[name].Value : null)));
                yield return ShellRecordUtilities.CreateExpando(fields);
            }
        }
    }
}

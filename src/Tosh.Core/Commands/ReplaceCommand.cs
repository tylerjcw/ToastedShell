using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

[CommandCategory("Text")]
[CommandExample("echo alpha-beta | replace beta BETA")]
[CommandExample("echo \"A1 B2\" | replace -r \"[0-9]\" \"#\"")]
public sealed class ReplaceCommand : ShellCommand
{
    public ReplaceCommand()
        : base("replace", "Replaces text in each input value.", "replace [-i] [-m] [-s] [-x] [--explicit-capture] [-r] <pattern|regex> <replacement> [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count < 2)
        {
            throw new InvalidOperationException("replace requires a pattern and replacement.");
        }

        var patternValue = parsed.Positionals[0];
        var replacement = parsed.Positionals[1]?.ToString() ?? string.Empty;
        IReadOnlyList<object?> inputs = parsed.Positionals.Count > 2
            ? parsed.Positionals.Skip(2).ToArray()
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (inputs.Count == 0)
        {
            yield break;
        }

        var ignoreCase = parsed.HasFlag("i", "ignore-case");
        var regexMode = parsed.HasFlag("r", "regex") || patternValue is Regex;

        if (!regexMode && ShellRegexUtilities.HasRegexOnlyModifierFlags(parsed))
        {
            throw new InvalidOperationException("replace regex modifier flags require -r or a regex pattern.");
        }

        var regex = regexMode
            ? ShellRegexUtilities.RequireRegex(context, parsed, patternValue, "pattern", timeout: TimeSpan.FromSeconds(5))
            : null;
        var pattern = regexMode ? null : CommandArguments.RequireString(parsed.Positionals, 0, "pattern");
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var input in inputs)
        {
            var text = input is ShellTextLine line ? line.Text : ExternalTextSerializer.Serialize(input);
            var output = regex is null
                ? text.Replace(pattern!, replacement, comparison)
                : regex.Replace(text, replacement);
            yield return new ShellTextLine(output);
        }
    }
}

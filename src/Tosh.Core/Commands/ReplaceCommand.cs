using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

public sealed class ReplaceCommand : ShellCommand
{
    public ReplaceCommand()
        : base("replace", "Replaces text in each input value.", "replace [-i] [-r] <pattern> <replacement> [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count < 2)
        {
            throw new InvalidOperationException("replace requires a pattern and replacement.");
        }

        var pattern = CommandArguments.RequireString(parsed.Positionals, 0, "pattern");
        var replacement = parsed.Positionals[1]?.ToString() ?? string.Empty;
        IReadOnlyList<object?> inputs = parsed.Positionals.Count > 2
            ? parsed.Positionals.Skip(2).ToArray()
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (inputs.Count == 0)
        {
            yield break;
        }

        var ignoreCase = parsed.HasFlag("i");
        var regexMode = parsed.HasFlag("r", "regex");
        var regex = regexMode
            ? new Regex(pattern, RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None))
            : null;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var input in inputs)
        {
            var text = input is ShellTextLine line ? line.Text : ExternalTextSerializer.Serialize(input);
            var output = regex is null
                ? text.Replace(pattern, replacement, comparison)
                : regex.Replace(text, replacement);
            yield return new ShellTextLine(output);
        }
    }
}

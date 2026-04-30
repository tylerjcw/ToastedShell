using System.Text.RegularExpressions;

namespace Tosh.Core.Commands.Text;

[Stdlib(StdlibCategory.Text)]
[CommandCategory("Text")]
[CommandArgument("delimiter|regex", "Delimiter string, regex pattern, or Regex object. Defaults to whitespace splitting when omitted.", Required = false)]
[CommandArgument("text ...", "Optional explicit text values. When omitted, reads pipeline text.", Required = false)]
[CommandOption("-r, --regex", "Treat the delimiter as a regular expression.")]
[CommandOption("-i, --ignore-case", "Use case-insensitive regex matching.")]
[CommandOption("-m, --multiline", "Enable regex multiline mode.")]
[CommandOption("-s, --singleline", "Enable regex singleline mode so . matches newlines.")]
[CommandOption("-x, --ignore-pattern-whitespace", "Ignore unescaped whitespace and allow # comments in the regex pattern.")]
[CommandOption("--explicit-capture", "Only capture explicitly named or numbered groups in regex mode.")]
[CommandExample("echo \"alpha,beta,gamma\" | split \",\"")]
[CommandExample("echo \"alpha,beta;gamma\" | split -r \"[,;]\"")]
[CommandOutput("ShellTextLine values — one per substring produced by the configured split.")]
public sealed class SplitCommand : ShellCommand
{
    public SplitCommand()
        : base("split", "Splits text values into smaller text values.", "split [-r] [-i] [-m] [-s] [-x] [--explicit-capture] [delimiter|regex] [text ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var delimiterValue = parsed.Positionals.Count > 0
            ? parsed.Positionals[0]
            : null;
        var regexMode = parsed.HasFlag("r", "regex") || delimiterValue is Regex;

        if (!regexMode && ShellRegexUtilities.HasModifierFlags(parsed))
        {
            throw new InvalidOperationException("split regex flags require -r or a regex delimiter.");
        }

        if (regexMode && delimiterValue is null)
        {
            throw new InvalidOperationException("split -r requires a delimiter or regex pattern.");
        }

        IReadOnlyList<object?> inputValues = parsed.Positionals.Count > 1
            ? parsed.Positionals.Skip(1).ToArray()
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        var regex = regexMode
            ? ShellRegexUtilities.RequireRegex(context, parsed, delimiterValue, "delimiter", timeout: TimeSpan.FromSeconds(5))
            : null;
        var delimiter = regexMode || delimiterValue is null
            ? null
            : CommandArguments.RequireString(parsed.Positionals, 0, "delimiter");

        foreach (var value in inputValues)
        {
            var text = value switch
            {
                ShellTextLine line => line.Text,
                _ => ExternalTextSerializer.Serialize(value),
            };

            IEnumerable<string> parts = regex is not null
                ? regex.Split(text)
                : delimiter is null
                    ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    : delimiter.Length == 0
                        ? text.Select(c => c.ToString())
                        : text.Split([delimiter], StringSplitOptions.None);

            foreach (var part in parts)
            {
                yield return new ShellTextLine(part);
            }
        }
    }
}

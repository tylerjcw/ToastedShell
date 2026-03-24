using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

public sealed class GrepCommand : ShellCommand
{
    public GrepCommand()
        : base("grep", "Searches text input with a regular expression or literal pattern.", "grep [-i] [-v] [-F] [-n] <pattern> [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments, context.Runtime.CurrentDirectory);
        var lines = options.Paths.Count > 0
            ? await TextInputUtilities.ReadLinesFromFilesAsync(options.Paths, context.CancellationToken)
            : await TextInputUtilities.ReadLinesFromInputAsync(context, "grep expects text input or file paths.");

        Regex? regex = null;

        if (!options.FixedString)
        {
            var regexOptions = RegexOptions.CultureInvariant;

            if (options.IgnoreCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            try
            {
                regex = new Regex(options.Pattern, regexOptions, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"Invalid regular expression pattern. {exception.Message}");
            }
        }

        foreach (var line in lines)
        {
            var matched = options.FixedString
                ? line.Text.Contains(options.Pattern, options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                : regex!.IsMatch(line.Text);

            if (options.InvertMatch ? !matched : matched)
            {
                var text = options.ShowLineNumbers ? $"{line.LineNumber}:{line.Text}" : line.Text;
                yield return new GrepMatchInfo(line.Path, line.LineNumber, text, options.Pattern);
            }
        }
    }

    private static GrepOptions ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var ignoreCase = false;
        var invertMatch = false;
        var fixedString = false;
        var showLineNumbers = false;
        string? pattern = null;
        var paths = new List<string>();

        foreach (var argument in arguments)
        {
            var text = argument?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (pattern is null && text.StartsWith("-", StringComparison.Ordinal))
            {
                switch (text)
                {
                    case "-i":
                        ignoreCase = true;
                        continue;
                    case "-v":
                        invertMatch = true;
                        continue;
                    case "-F":
                        fixedString = true;
                        continue;
                    case "-n":
                        showLineNumbers = true;
                        continue;
                }

                throw new InvalidOperationException($"Unsupported grep option '{text}'.");
            }

            if (pattern is null)
            {
                pattern = text;
                continue;
            }

            paths.Add(PathUtilities.ResolvePath(currentDirectory, text));
        }

        if (pattern is null)
        {
            throw new InvalidOperationException("grep requires a pattern.");
        }

        return new GrepOptions(pattern, ignoreCase, invertMatch, fixedString, showLineNumbers, paths);
    }

    private sealed record GrepOptions(
        string Pattern,
        bool IgnoreCase,
        bool InvertMatch,
        bool FixedString,
        bool ShowLineNumbers,
        IReadOnlyList<string> Paths);
}

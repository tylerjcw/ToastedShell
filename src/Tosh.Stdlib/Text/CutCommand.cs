using Tosh.Runtime;

namespace Tosh.Stdlib.Text;

[CommandCategory("Text")]
[CommandArgument("path ...", "Optional files to read instead of pipeline input.", Required = false, TypeName = "path-like")]
[CommandOption("-f <fields>", "Select 1-based delimited fields. Supports comma-separated values and ranges such as 1,3-5.")]
[CommandOption("-d <delimiter>", "Field delimiter for -f mode. Defaults to tab.", Default = "\\t")]
[CommandOption("-c <chars>", "Select 1-based character positions. Supports comma-separated values and ranges such as 1,3-5.")]
[CommandExample("echo \"alpha,beta,gamma\" | cut -d , -f 2", Title = "Extract a delimited field")]
[CommandExample("echo \"abcdef\" | cut -c 2-4", Title = "Extract character positions")]
[CommandOutput("ShellTextLine values containing the selected character ranges or delimited fields, one per input line.")]
public sealed class CutCommand : ShellCommand
{
    public CutCommand()
        : base("cut", "Extracts character or delimited fields from text.", "cut (-f fields [-d delimiter] | -c chars) [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments, context.Runtime.CurrentDirectory);
        var lines = options.Paths.Count > 0
            ? await TextInputUtilities.ReadLinesFromFilesAsync(options.Paths, context.CancellationToken)
            : await TextInputUtilities.ReadLinesFromInputAsync(context, "cut expects text input or file paths.");

        foreach (var line in lines)
        {
            yield return new ShellTextLine(options.Mode switch
            {
                CutMode.Fields => CutFields(line.Text, options.Delimiter!, options.Ranges),
                CutMode.Characters => CutCharacters(line.Text, options.Ranges),
                _ => line.Text,
            });
        }
    }

    private static string CutFields(string text, string delimiter, IReadOnlyList<(int Start, int End)> ranges)
    {
        var fields = text.Split(delimiter);
        var selected = new List<string>();

        foreach (var (start, end) in ranges)
        {
            for (var index = start; index <= end && index <= fields.Length; index++)
            {
                if (index >= 1)
                {
                    selected.Add(fields[index - 1]);
                }
            }
        }

        return string.Join(delimiter, selected);
    }

    private static string CutCharacters(string text, IReadOnlyList<(int Start, int End)> ranges)
    {
        var characters = new List<char>();

        foreach (var (start, end) in ranges)
        {
            for (var index = start; index <= end && index <= text.Length; index++)
            {
                if (index >= 1)
                {
                    characters.Add(text[index - 1]);
                }
            }
        }

        return new string(characters.ToArray());
    }

    private static CutOptions ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        string? delimiter = "\t";
        IReadOnlyList<(int Start, int End)>? ranges = null;
        var mode = CutMode.None;
        var pathArguments = new List<object?>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            switch (text)
            {
                case "-d":
                    delimiter = arguments.ElementAtOrDefault(++index)?.ToString()
                                ?? throw new InvalidOperationException("Missing required argument: delimiter.");
                    break;
                case "-f":
                    mode = CutMode.Fields;
                    ranges = ParseRanges(arguments.ElementAtOrDefault(++index)?.ToString()
                                         ?? throw new InvalidOperationException("Missing required argument: fields."));
                    break;
                case "-c":
                    mode = CutMode.Characters;
                    ranges = ParseRanges(arguments.ElementAtOrDefault(++index)?.ToString()
                                         ?? throw new InvalidOperationException("Missing required argument: characters."));
                    break;
                default:
                    if (text.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Unsupported cut option '{text}'.");
                    }

                    pathArguments.Add(arguments[index]);
                    break;
            }
        }

        if (mode == CutMode.None || ranges is null)
        {
            throw new InvalidOperationException("cut requires either -f or -c.");
        }

        return new CutOptions(mode, delimiter, ranges, ShellPathArguments.ExpandMany(currentDirectory, pathArguments));
    }

    private static IReadOnlyList<(int Start, int End)> ParseRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', 2);

            if (bounds.Length == 1 && int.TryParse(bounds[0], out var value))
            {
                ranges.Add((value, value));
                continue;
            }

            if (bounds.Length == 2 &&
                int.TryParse(bounds[0], out var start) &&
                int.TryParse(bounds[1], out var end))
            {
                ranges.Add((Math.Min(start, end), Math.Max(start, end)));
                continue;
            }

            throw new InvalidOperationException($"'{part}' is not a valid cut range.");
        }

        return ranges;
    }

    private enum CutMode
    {
        None,
        Fields,
        Characters,
    }

    private sealed record CutOptions(
        CutMode Mode,
        string? Delimiter,
        IReadOnlyList<(int Start, int End)> Ranges,
        IReadOnlyList<string> Paths);
}

using System.Text;

using Tosh.Runtime;

namespace Tosh.Stdlib.Text;

[CommandCategory("Text")]
[CommandArgument("path ...", "Optional file paths to measure. When omitted, `wc` measures the current text pipeline.", Required = false, TypeName = "path-like")]
[CommandOption("-l", "Show line counts.")]
[CommandOption("-w", "Show word counts.")]
[CommandOption("-c", "Show byte counts.")]
[CommandOption("-m", "Show character counts.")]
[CommandOption("-L", "Show the longest-line length.")]
[CommandExample("wc README.md")]
[CommandExample("wc -lwm README.md")]
[CommandExample("echo one two three | wc")]
[CommandNote("Wc returns typed statistics objects instead of formatted text, so you can still `get`, `where`, or `summarize` them later. Selector flags like `-l` and `-w` only change the visible columns, not the underlying objects.")]
[CommandOutput("Returns typed text-statistics objects, and appends a `total` row when multiple files are counted.")]
[CommandSideEffects(ReadsFiles = true)]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the current text pipeline when explicit file paths are omitted.")]
public sealed class WordCountCommand : ShellCommand
{
    public WordCountCommand()
        : base("wc", "Counts lines, words, bytes, characters, and longest-line length.", "wc [-lwmcL] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var implicitSelection = BuildImplicitSelection(parsed);

        if (parsed.Positionals.Count > 0)
        {
            var paths = ShellPathArguments.ExpandMany(context.LanguageRuntime.CurrentDirectory, parsed.Positionals);
            var statistics = new List<TextStatistics>();

            foreach (var path in paths)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"File '{path}' does not exist.");
                }

                var fileText = await File.ReadAllTextAsync(path, context.CancellationToken);
                var stats = CreateStatistics(path, fileText, new FileInfo(path).Length);
                statistics.Add(stats);
                yield return ApplySelection(context, implicitSelection, stats);
            }

            if (statistics.Count > 1)
            {
                yield return ApplySelection(context, implicitSelection, CreateTotalStatistics(statistics));
            }

            yield break;
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            throw new InvalidOperationException("wc expects text input or file paths.");
        }

        var text = string.Join(Environment.NewLine, values.Select(ExternalTextSerializer.Serialize));
        var bytes = Encoding.UTF8.GetByteCount(text);
        yield return ApplySelection(context, implicitSelection, CreateStatistics(null, text, bytes));
    }

    private static TextStatistics CreateStatistics(string? path, string text, long bytes)
    {
        var lines = TextInputUtilities.SplitLines(text).ToArray();
        return new TextStatistics(
            path,
            lines.Length,
            TextInputUtilities.CountWords(text),
            bytes,
            text.Length,
            lines.Length == 0 ? 0 : lines.Max(line => line.Length));
    }

    private static TextStatistics CreateTotalStatistics(IReadOnlyList<TextStatistics> statistics)
    {
        checked
        {
            return new TextStatistics(
                "total",
                statistics.Sum(item => item.Lines),
                statistics.Sum(item => item.Words),
                statistics.Sum(item => item.Bytes),
                statistics.Sum(item => item.Characters),
                statistics.Count == 0 ? 0 : statistics.Max(item => item.LongestLine),
                IsTotal: true);
        }
    }

    private static DisplayColumnSelection BuildImplicitSelection(ParsedCommandArguments parsed)
    {
        var showColumns = new List<string>();

        if (parsed.HasFlag("l"))
        {
            showColumns.Add("Path");
            showColumns.Add("Lines");
        }

        if (parsed.HasFlag("w"))
        {
            showColumns.Add("Path");
            showColumns.Add("Words");
        }

        if (parsed.HasFlag("c"))
        {
            showColumns.Add("Path");
            showColumns.Add("Bytes");
        }

        if (parsed.HasFlag("m"))
        {
            showColumns.Add("Path");
            showColumns.Add("Chars");
        }

        if (parsed.HasFlag("L"))
        {
            showColumns.Add("Path");
            showColumns.Add("MaxLine");
        }

        return new DisplayColumnSelection(showColumns, [], showAll: false);
    }

    private static object ApplySelection(CommandContext context, DisplayColumnSelection selection, TextStatistics statistics)
    {
        return selection.HasOverrides
            ? CommandDisplaySelectionParser.Apply(context.Runtime, selection, statistics)!
            : statistics;
    }
}

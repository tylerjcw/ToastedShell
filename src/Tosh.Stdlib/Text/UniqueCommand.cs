using Tosh.Runtime;

namespace Tosh.Stdlib.Text;

[CommandCategory("Text")]
[CommandOption("-c", "Prefix each line with its count of consecutive occurrences.")]
[CommandOption("-i", "Compare lines case-insensitively.")]
[CommandArgument("path", "Optional file path(s) to read instead of pipeline input.", Required = false)]
[CommandExample("echo a a b b b c | uniq", Title = "Collapse adjacent duplicates")]
[CommandExample("echo a a b b b c | uniq -c", Title = "Count consecutive occurrences")]
[CommandOutput("Unique adjacent values, optionally prefixed with counts.")]
[PipelineInput(AcceptsScalar = true, Description = "Reads text lines from the pipeline or from file arguments.")]
public sealed class UniqueCommand : ShellCommand
{
    public UniqueCommand()
        : base("uniq", "Collapses adjacent duplicate input values.", "uniq [-c] [-i] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var countOccurrences = parsed.HasFlag("c");
        var ignoreCase = parsed.HasFlag("i");

        if (parsed.Positionals.Count > 0)
        {
            var paths = ShellPathArguments.ExpandMany(context.LanguageRuntime.CurrentDirectory, parsed.Positionals);
            var lines = await TextInputUtilities.ReadLinesFromFilesAsync(paths, context.CancellationToken);

            foreach (var item in Collapse(lines.Select(line => (object?)new ShellTextLine(line.Text)).ToList(), countOccurrences, ignoreCase))
            {
                yield return item;
            }

            yield break;
        }

        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        foreach (var item in Collapse(input, countOccurrences, ignoreCase))
        {
            yield return item;
        }
    }

    private static IEnumerable<object?> Collapse(IReadOnlyList<object?> input, bool countOccurrences, bool ignoreCase)
    {
        if (input.Count == 0)
        {
            yield break;
        }

        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var current = input[0];
        var currentKey = ExternalTextSerializer.Serialize(current);
        var count = 1;

        for (var index = 1; index < input.Count; index++)
        {
            var candidate = input[index];
            var candidateKey = ExternalTextSerializer.Serialize(candidate);

            if (comparer.Equals(currentKey, candidateKey))
            {
                count++;
                continue;
            }

            yield return countOccurrences ? CreateCountProjection(count, current) : current;
            current = candidate;
            currentKey = candidateKey;
            count = 1;
        }

        yield return countOccurrences ? CreateCountProjection(count, current) : current;
    }

    private static System.Dynamic.ExpandoObject CreateCountProjection(int count, object? value)
    {
        return ShellRecordUtilities.CreateExpando(
        [
            new KeyValuePair<string, object?>("Count", count),
            new KeyValuePair<string, object?>("Value", value),
        ]);
    }
}

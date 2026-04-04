using System.Dynamic;

namespace Tosh.Core.Commands;

public sealed class CatCommand : ShellCommand
{
    public CatCommand()
        : base("cat", "Reads one or more files or piped text sources and emits their contents.", "cat [-n|-b] [-s] [-E] [-T] [-A] [path ...|-]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var showAll = parsed.HasFlag("A", "show-all");
        var numberAllLines = parsed.HasFlag("n");
        var numberNonBlank = parsed.HasFlag("b");
        var squeezeBlank = parsed.HasFlag("s");
        var showEnds = showAll || parsed.HasFlag("E", "show-ends");
        var showTabs = showAll || parsed.HasFlag("T", "show-tabs");
        var inputValues = parsed.Positionals.Count == 0
            ? await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken)
            : [];
        var sources = CollectPathSources(context, parsed.Positionals, inputValues);

        var nextLineNumber = 1;
        var previousWasBlank = false;

        if (sources.Count > 0)
        {
            foreach (var source in sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<TextInputLine> lines;

                if (string.Equals(source, "-", StringComparison.Ordinal))
                {
                    lines = parsed.Positionals.Count == 0
                        ? ReadLinesFromValues(inputValues)
                        : await TextInputUtilities.ReadLinesFromInputAsync(
                            context,
                            "cat '-' requires piped text input.");
                }
                else
                {
                    lines = await TextInputUtilities.ReadLinesFromFilesAsync([source], context.CancellationToken);
                }

                foreach (var line in EmitLines(lines, numberAllLines, numberNonBlank, squeezeBlank, showEnds, showTabs, ref nextLineNumber, ref previousWasBlank))
                {
                    yield return line;
                }
            }

            yield break;
        }

        if (inputValues.Count == 0)
        {
            throw new InvalidOperationException("cat requires at least one path or pipeline input.");
        }

        foreach (var line in EmitLines(ReadLinesFromValues(inputValues), numberAllLines, numberNonBlank, squeezeBlank, showEnds, showTabs, ref nextLineNumber, ref previousWasBlank))
        {
            yield return line;
        }
    }

    private static IReadOnlyList<string> CollectPathSources(
        CommandContext context,
        IReadOnlyList<object?> positionals,
        IReadOnlyList<object?> inputValues)
    {
        if (positionals.Count > 0)
        {
            var explicitSources = new List<string>();

            foreach (var positional in positionals)
            {
                if (positional is string text && string.Equals(text, "-", StringComparison.Ordinal))
                {
                    explicitSources.Add("-");
                }
                else
                {
                    explicitSources.AddRange(ShellPathArguments.Expand(context.Runtime.CurrentDirectory, positional));
                }
            }

            return explicitSources;
        }

        if (inputValues.Count == 0)
        {
            return Array.Empty<string>();
        }

        var sources = new List<string>();

        foreach (var input in inputValues)
        {
            if (input is string text && string.Equals(text, "-", StringComparison.Ordinal))
            {
                sources.Add("-");
                continue;
            }

            if (!TryExpandExistingFilePath(context.Runtime.CurrentDirectory, input, out var expanded))
            {
                return Array.Empty<string>();
            }

            sources.AddRange(expanded);
        }

        return sources;
    }

    private static bool TryExpandExistingFilePath(string currentDirectory, object? value, out IReadOnlyList<string> paths)
    {
        try
        {
            paths = ShellPathArguments.Expand(currentDirectory, value);
        }
        catch (InvalidOperationException)
        {
            paths = Array.Empty<string>();
            return false;
        }

        return paths.All(File.Exists);
    }

    private static IReadOnlyList<TextInputLine> ReadLinesFromValues(IReadOnlyList<object?> values)
    {
        var lines = new List<TextInputLine>();
        var lineNumber = 1;

        foreach (var value in values)
        {
            AddLines(lines, ExternalTextSerializer.Serialize(value), ref lineNumber);
        }

        return lines;
    }

    private static IReadOnlyList<object?> EmitLines(
        IReadOnlyList<TextInputLine> lines,
        bool numberAllLines,
        bool numberNonBlank,
        bool squeezeBlank,
        bool showEnds,
        bool showTabs,
        ref int nextLineNumber,
        ref bool previousWasBlank)
    {
        var results = new List<object?>();

        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrEmpty(line.Text);

            if (squeezeBlank && isBlank && previousWasBlank)
            {
                continue;
            }

            previousWasBlank = isBlank;

            var text = line.Text;

            if (showTabs)
            {
                text = text.Replace("\t", "^I");
            }

            if (showEnds)
            {
                text += "$";
            }

            if (numberAllLines || numberNonBlank)
            {
                results.Add(CreateLineRecord(
                    number: numberNonBlank && isBlank ? null : nextLineNumber++,
                    text: text));
                continue;
            }

            results.Add(new ShellTextLine(text));
        }

        return results;
    }

    private static void AddLines(List<TextInputLine> lines, string text, ref int lineNumber)
    {
        var emitted = false;

        foreach (var line in TextInputUtilities.SplitLines(text))
        {
            lines.Add(new TextInputLine(line, null, lineNumber));
            lineNumber++;
            emitted = true;
        }

        if (!emitted && text.Length == 0)
        {
            lines.Add(new TextInputLine(string.Empty, null, lineNumber));
            lineNumber++;
        }
    }

    private static IDictionary<string, object?> CreateLineRecord(long? number, string text)
    {
        IDictionary<string, object?> record = new ExpandoObject();
        record["Number"] = number;
        record["Text"] = text;
        return record;
    }
}

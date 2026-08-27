using System.Dynamic;

using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path ...|-", "One or more file paths to concatenate, or `-` to read piped text input explicitly.", Required = false, TypeName = "path-like|string")]
[CommandOption("-n", "Number every emitted line.")]
[CommandOption("-b", "Number only non-blank lines.")]
[CommandOption("-v", "Display non-printing characters as `^X` or `M-X`.")]
[CommandOption("-s", "Squeeze repeated blank lines into a single blank line.")]
[CommandOption("-e", "Equivalent to `-vE`.")]
[CommandOption("-t", "Equivalent to `-vT`.")]
[CommandExample("cat README.md")]
[CommandExample("echo alpha beta | cat -")]
[CommandExample("cat -n README.md")]
[CommandNote("With no explicit paths, `cat` treats piped values as file paths only when every value resolves to an existing file. Otherwise it treats the pipeline as text input. Use `-` explicitly when mixing file paths with piped text.")]
[CommandOutput("Returns plain text lines by default, or numbered record rows with `Number` and `Text` when `-n` or `-b` is used.", Mode = "mixed")]
[CommandSideEffects(ReadsFiles = true)]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, AcceptsList = true, Description = "With no explicit paths, path-like pipeline values are treated as files when they all resolve to existing files; otherwise pipeline values are treated as text input. Use `-` explicitly when you want stdin-style text alongside file arguments.")]
public sealed class CatCommand : ShellCommand
{
    public CatCommand()
        : base("cat", "Reads one or more files or piped text sources and emits their contents.", "cat [-n|-b] [-svETAet] [path ...|-]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var showAll = parsed.HasFlag("A", "show-all");
        var showE = parsed.HasFlag("e");
        var showT = parsed.HasFlag("t");
        var numberAllLines = parsed.HasFlag("n");
        var numberNonBlank = parsed.HasFlag("b");
        var squeezeBlank = parsed.HasFlag("s");
        var showEnds = showAll || showE || parsed.HasFlag("E", "show-ends");
        var showTabs = showAll || showT || parsed.HasFlag("T", "show-tabs");
        var showNonPrinting = showAll || showE || showT || parsed.HasFlag("v", "show-nonprinting");
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

                foreach (var line in EmitLines(lines, numberAllLines, numberNonBlank, squeezeBlank, showEnds, showTabs, showNonPrinting, ref nextLineNumber, ref previousWasBlank))
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

        foreach (var line in EmitLines(ReadLinesFromValues(inputValues), numberAllLines, numberNonBlank, squeezeBlank, showEnds, showTabs, showNonPrinting, ref nextLineNumber, ref previousWasBlank))
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
                    explicitSources.AddRange(ShellPathArguments.Expand(context.Shell().CurrentDirectory, positional));
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

            if (!TryExpandExistingFilePath(context.Shell().CurrentDirectory, input, out var expanded))
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
        bool showNonPrinting,
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

            if (showNonPrinting)
            {
                text = RenderNonPrinting(text, excludeTab: showTabs);
            }

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

    private static string RenderNonPrinting(string text, bool excludeTab)
    {
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (var ch in text)
        {
            if (ch == '\t')
            {
                // Tab is handled separately by showTabs; pass through here.
                builder.Append(ch);
            }
            else if (ch < 0x20) // Control characters 0x00–0x1F
            {
                builder.Append('^');
                builder.Append((char)(ch + 0x40));
            }
            else if (ch == 0x7F) // DEL
            {
                builder.Append("^?");
            }
            else if (ch >= 0x80 && ch <= 0x9F) // High-bit control
            {
                builder.Append("M-^");
                builder.Append((char)(ch - 0x40));
            }
            else if (ch >= 0xA0 && ch <= 0xFE) // High-bit printable
            {
                builder.Append("M-");
                builder.Append((char)(ch - 0x80));
            }
            else if (ch == 0xFF)
            {
                builder.Append("M-^?");
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
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

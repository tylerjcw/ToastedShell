using System.Dynamic;
using System.Text.RegularExpressions;

namespace Tosh.Core.Commands;

[CommandCategory("Text")]
[CommandArgument("pattern|regex", "The text pattern or .NET regular expression to search for.", TypeName = "string|regex")]
[CommandArgument("path", "Optional file paths to search instead of consuming piped text.", Required = false, TypeName = "path-like")]
[CommandOption("-i", "Ignore case.")]
[CommandOption("-v", "Invert the match.")]
[CommandOption("-F", "Treat the pattern as a literal string instead of a regex.")]
[CommandOption("-n", "Include source line numbers in text-file results.")]
[CommandOption("-m", "Multiline mode.")]
[CommandOption("-s", "Singleline mode so '.' matches newlines.")]
[CommandOption("-x", "Require a full-line match.")]
[CommandOption("--explicit-capture", "Return structured capture results instead of plain matching lines.")]
[CommandExample("echo one two three | grep tw", Title = "Pipe text into grep")]
[CommandExample("echo \"Alpha\" | grep -i \"^alpha$\"", Title = "Use regex flags")]
[CommandExample("grep -F literal README.md", Title = "Search a file literally")]
[CommandOutput("Matching text lines, or structured regex capture objects with --explicit-capture.", TypeName = "GrepMatchInfo", Members = "Path, LineNumber, Text, Pattern, Match", Mode = "mixed")]
[CommandSideEffects(ReadsFiles = true)]
[PipelineInput(AcceptsScalar = true, Description = "Consumes scalar text from the pipeline. When paths are supplied explicitly, grep reads file contents instead.")]
public sealed class GrepCommand : ShellCommand
{
    public GrepCommand()
        : base("grep", "Searches text input with a regular expression or literal pattern.",
            "grep [-i] [-v] [-F] [-n] [-r] [-w] [-o] [-c] [-l] [-L] [-A num] [-B num] [-C num] <pattern|regex> [path ...]")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context, context.Arguments, context.Runtime.CurrentDirectory);
        var lines = options.Paths.Count > 0
            ? await ReadLinesAsync(options.Paths, options.Recursive, context.CancellationToken)
            : await TextInputUtilities.ReadLinesFromInputAsync(context, "grep expects text input or file paths.");

        if (options.CountOnly)
        {
            await foreach (var result in EmitCountsAsync(lines, options))
            {
                yield return result;
            }

            yield break;
        }

        if (options.FilesWithMatch || options.FilesWithoutMatch)
        {
            await foreach (var result in EmitFileNamesAsync(lines, options))
            {
                yield return result;
            }

            yield break;
        }

        var linesByPath = GroupLinesByPath(lines);

        foreach (var (path, pathLines) in linesByPath)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < pathLines.Count; i++)
            {
                var line = pathLines[i];

                var (matched, matchText) = TestMatch(line.Text, options);

                if (options.InvertMatch ? !matched : matched)
                {
                    var displayText = options.OnlyMatching && matchText is not null
                        ? matchText
                        : options.ShowLineNumbers
                            ? $"{line.LineNumber}:{line.Text}"
                            : line.Text;

                    var contextBefore = options.BeforeContext > 0
                        ? GatherContext(pathLines, i, -options.BeforeContext, options)
                        : null;
                    var contextAfter = options.AfterContext > 0
                        ? GatherContext(pathLines, i, options.AfterContext, options)
                        : null;

                    var result = new GrepMatchInfo(
                        line.Path, line.LineNumber, displayText, options.PatternText,
                        Match: matchText, ContextBefore: contextBefore, ContextAfter: contextAfter);
                    yield return result;
                }
            }
        }
    }

    private static (bool Matched, string? MatchText) TestMatch(string text, GrepOptions options)
    {
        if (options.FixedString)
        {
            var comparison = options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (options.WordMatch)
            {
                // For word match with fixed string, use regex word boundaries
                var escapedPattern = Regex.Escape(options.PatternText);
                var wordRegex = new Regex(
                    $@"\b{escapedPattern}\b",
                    RegexOptions.Compiled | RegexOptions.CultureInvariant |
                    (options.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                    TimeSpan.FromSeconds(5));
                var m = wordRegex.Match(text);
                return m.Success ? (true, m.Value) : (false, null);
            }

            var idx = text.IndexOf(options.PatternText, comparison);
            return idx >= 0 ? (true, options.PatternText) : (false, null);
        }

        var match = options.Regex!.Match(text);
        return match.Success ? (true, match.Value) : (false, null);
    }

    private static IReadOnlyList<string>? GatherContext(
        IReadOnlyList<TextInputLine> lines, int matchIndex, int count, GrepOptions options)
    {
        var result = new List<string>();

        if (count < 0)
        {
            // Before context
            var start = Math.Max(0, matchIndex + count);

            for (var i = start; i < matchIndex; i++)
            {
                result.Add(lines[i].Text);
            }
        }
        else
        {
            // After context
            var end = Math.Min(lines.Count - 1, matchIndex + count);

            for (var i = matchIndex + 1; i <= end; i++)
            {
                result.Add(lines[i].Text);
            }
        }

        return result.Count > 0 ? result : null;
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<object?> EmitCountsAsync(
        IReadOnlyList<TextInputLine> lines, GrepOptions options)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var key = line.Path ?? "(stdin)";
            var (matched, _) = TestMatch(line.Text, options);
            var isMatch = options.InvertMatch ? !matched : matched;

            if (!counts.ContainsKey(key))
            {
                counts[key] = 0;
            }

            if (isMatch)
            {
                counts[key]++;
            }
        }

        foreach (var (path, count) in counts)
        {
            IDictionary<string, object?> record = new ExpandoObject();
            record["Path"] = path == "(stdin)" ? null : path;
            record["Count"] = count;
            yield return record;
        }
    }

    private static async IAsyncEnumerable<object?> EmitFileNamesAsync(
        IReadOnlyList<TextInputLine> lines, GrepOptions options)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var filesWithMatch = new HashSet<string>(StringComparer.Ordinal);
        var allFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var key = line.Path ?? "(stdin)";
            allFiles.Add(key);

            if (filesWithMatch.Contains(key))
            {
                continue;
            }

            var (matched, _) = TestMatch(line.Text, options);
            var isMatch = options.InvertMatch ? !matched : matched;

            if (isMatch)
            {
                filesWithMatch.Add(key);
            }
        }

        var targetSet = options.FilesWithMatch ? filesWithMatch : allFiles.Except(filesWithMatch);

        foreach (var path in targetSet)
        {
            if (seen.Add(path))
            {
                yield return path == "(stdin)" ? null : path;
            }
        }
    }
#pragma warning restore CS1998

    private static IReadOnlyList<(string? Path, IReadOnlyList<TextInputLine> Lines)> GroupLinesByPath(
        IReadOnlyList<TextInputLine> lines)
    {
        var groups = new List<(string? Path, List<TextInputLine> Lines)>();
        string? currentPath = "\x00SENTINEL";

        foreach (var line in lines)
        {
            if (!string.Equals(line.Path, currentPath, StringComparison.Ordinal) ||
                currentPath == "\x00SENTINEL")
            {
                currentPath = line.Path;
                groups.Add((line.Path, new List<TextInputLine> { line }));
            }
            else
            {
                groups[^1].Lines.Add(line);
            }
        }

        return groups.Select(g => ((string?)g.Path, (IReadOnlyList<TextInputLine>)g.Lines)).ToList();
    }

    private static async Task<IReadOnlyList<TextInputLine>> ReadLinesAsync(
        IReadOnlyList<string> paths, bool recursive, CancellationToken cancellationToken)
    {
        if (!recursive)
        {
            return await TextInputUtilities.ReadLinesFromFilesAsync(paths, cancellationToken);
        }

        var expandedPaths = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                expandedPaths.AddRange(
                    Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            }
            else
            {
                expandedPaths.Add(path);
            }
        }

        return await TextInputUtilities.ReadLinesFromFilesAsync(expandedPaths, cancellationToken);
    }

    private static GrepOptions ParseOptions(CommandContext context, IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var positionals = new List<object?>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var beforeContext = 0;
        var afterContext = 0;
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (!parseOptions || text is null || text.Length == 0)
            {
                positionals.Add(arguments[index]);
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            switch (text)
            {
                case "-A" or "--after-context":
                    afterContext = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                    continue;
                case "-B" or "--before-context":
                    beforeContext = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                    continue;
                case "-C" or "--context":
                    var ctx = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                    beforeContext = ctx;
                    afterContext = ctx;
                    continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal) && text.Length > 2)
            {
                flags.Add(text[2..]);
                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal) && text.Length > 1 && !text.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (var flag in text[1..])
                {
                    flags.Add(flag.ToString());
                }

                continue;
            }

            positionals.Add(arguments[index]);
        }

        if (positionals.Count == 0)
        {
            throw new InvalidOperationException("grep requires a pattern.");
        }

        var patternValue = positionals[0];
        var fixedString = flags.Contains("F") || flags.Contains("fixed") || flags.Contains("fixed-string") || flags.Contains("fixed-strings");
        var ignoreCase = flags.Contains("i") || flags.Contains("ignore-case");
        var wordMatch = flags.Contains("w") || flags.Contains("word-regexp");

        if (fixedString && patternValue is Regex)
        {
            throw new InvalidOperationException("grep -F requires a string pattern, not a regex value.");
        }

        var parsedForRegex = ParsedCommandArguments.Parse(arguments);

        if (fixedString && ShellRegexUtilities.HasRegexOnlyModifierFlags(parsedForRegex))
        {
            throw new InvalidOperationException("grep -F does not use regex-only flags like -m, -s, or -x.");
        }

        Regex? regex = null;

        if (!fixedString)
        {
            if (patternValue is Regex existingRegex && !ShellRegexUtilities.HasModifierFlags(parsedForRegex) && !wordMatch)
            {
                // Use the pre-compiled regex directly to preserve its options.
                regex = existingRegex;
            }
            else
            {
                var patternText = ShellRegexUtilities.RequirePatternText(patternValue, "pattern");

                var finalPattern = wordMatch ? $@"\b(?:{patternText})\b" : patternText;

                // Build regex from pattern with modifiers
                var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;

                if (ignoreCase)
                {
                    regexOptions |= RegexOptions.IgnoreCase;
                }

                if (flags.Contains("m") || flags.Contains("multiline"))
                {
                    regexOptions |= RegexOptions.Multiline;
                }

                if (flags.Contains("s") || flags.Contains("singleline") || flags.Contains("dotall"))
                {
                    regexOptions |= RegexOptions.Singleline;
                }

                if (flags.Contains("x") || flags.Contains("ignore-pattern-whitespace"))
                {
                    regexOptions |= RegexOptions.IgnorePatternWhitespace;
                }

                if (flags.Contains("explicit-capture"))
                {
                    regexOptions |= RegexOptions.ExplicitCapture;
                }

                regex = ShellRegexUtilities.CompileRegex(context, finalPattern, regexOptions, 0, TimeSpan.FromSeconds(5));
            }
        }

        return new GrepOptions(
            Regex: regex,
            PatternText: ShellRegexUtilities.RequirePatternText(patternValue, "pattern"),
            IgnoreCase: ignoreCase,
            InvertMatch: flags.Contains("v") || flags.Contains("invert-match"),
            FixedString: fixedString,
            ShowLineNumbers: flags.Contains("n") || flags.Contains("line-numbers"),
            Recursive: flags.Contains("r") || flags.Contains("R") || flags.Contains("recursive"),
            WordMatch: wordMatch,
            OnlyMatching: flags.Contains("o") || flags.Contains("only-matching"),
            CountOnly: flags.Contains("c") || flags.Contains("count"),
            FilesWithMatch: flags.Contains("l") || flags.Contains("files-with-matches"),
            FilesWithoutMatch: flags.Contains("L") || flags.Contains("files-without-match"),
            BeforeContext: beforeContext,
            AfterContext: afterContext,
            Paths: ShellPathArguments.ExpandMany(currentDirectory, positionals.Skip(1).ToArray()));
    }

    private sealed record GrepOptions(
        Regex? Regex,
        string PatternText,
        bool IgnoreCase,
        bool InvertMatch,
        bool FixedString,
        bool ShowLineNumbers,
        bool Recursive,
        bool WordMatch,
        bool OnlyMatching,
        bool CountOnly,
        bool FilesWithMatch,
        bool FilesWithoutMatch,
        int BeforeContext,
        int AfterContext,
        IReadOnlyList<string> Paths);
}

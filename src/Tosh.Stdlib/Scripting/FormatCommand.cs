using Tosh.Language.Formatting;
using Tosh.Runtime;

namespace Tosh.Stdlib.Scripting;

/// <summary>
/// <c>format</c> — pretty-print one or more tosh source files.
///
/// <para>
/// Default behaviour rewrites the files in place. Use <c>--check</c>
/// to verify formatting without writing (exits non-zero if any file
/// would change). Use <c>--stdout</c> to emit the formatted text to
/// standard output and leave the source file unchanged. Pass <c>-</c>
/// as the only argument to read from standard input.
/// </para>
/// </summary>
[CommandCategory("Scripting")]
[Stdlib(StdlibCategory.Scripting)]
[CommandArgument("path", "One or more tosh source files. Use '-' to read stdin.", TypeName = "path-like")]
[CommandOption("--check", "Do not write; exit non-zero if any file would change.")]
[CommandOption("--stdout", "Print formatted output to stdout instead of rewriting the file.")]
[CommandOption("--diff", "Print a unified-style diff of the changes that would be applied.")]
[CommandExample("format script.tosh", Title = "Rewrite a single file in place")]
[CommandExample("format --check src/**/*.tosh", Title = "Verify a tree without writing")]
[CommandExample("cat script.tosh | format -", Title = "Format stdin and emit to stdout")]
[CommandOutput("Yields one record per file with Path, Changed, and (in --check mode) Match status.")]
[CommandSideEffects(WritesFiles = true)]
public sealed class FormatCommand : ShellCommand
{
    public FormatCommand()
        : base("format", "Pretty-prints tosh source files.", "format [--check] [--stdout] [--diff] <path>...") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments);

        if (options.Paths.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.format.no_paths",
                title: "format requires at least one file path or '-' for stdin.",
                label: "pass a path like 'script.tosh' or '-' to read stdin");
        }

        // Stdin mode: single '-' path → read all of stdin, format, write to stdout.
        if (options.Paths.Count == 1 && options.Paths[0] == "-")
        {
            var input = await Console.In.ReadToEndAsync(context.CancellationToken);
            var stdinResult = ToshFormatter.Format(input, "<stdin>");
            if (!stdinResult.IsSyntacticallyValid)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.format.parse_error",
                    title: "Cannot format input: it does not parse.",
                    label: stdinResult.ParseDiagnostics[0].Title);
            }
            await Console.Out.WriteAsync(stdinResult.FormattedText);
            // Stdin mode: don't yield a record — formatted text is the
            // only output the caller wants on stdout.
            yield break;
        }

        var anyChanged = false;
        foreach (var path in options.Paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                throw context.CreateDiagnostic(
                    code: "tosh.format.file_not_found",
                    title: $"format: file '{path}' does not exist.",
                    label: "check the path and try again");
            }

            var original = await File.ReadAllTextAsync(path, context.CancellationToken);
            var result = ToshFormatter.Format(original, path);

            if (!result.IsSyntacticallyValid)
            {
                // Emit a diagnostic record but don't throw — let the caller
                // process the rest of the batch. Surface the first parse error.
                var firstError = result.ParseDiagnostics[0];
                await Console.Error.WriteLineAsync(
                    $"format: '{path}' has parse errors and was not formatted: {firstError.Title}");
                yield return new FormatResultRecord(path, Changed: false, Formatted: false);
                continue;
            }

            var changed = original != result.FormattedText;
            anyChanged |= changed;

            if (options.Diff && changed)
            {
                EmitUnifiedDiff(path, original, result.FormattedText);
            }

            if (changed && !options.CheckOnly && !options.StdoutOnly)
            {
                await File.WriteAllTextAsync(path, result.FormattedText, context.CancellationToken);
            }

            if (options.StdoutOnly)
            {
                await Console.Out.WriteAsync(result.FormattedText);
                // In --stdout mode, suppress the per-file record so the
                // formatted text is the only thing on stdout. The
                // pipeline auto-renderer would otherwise append a table
                // and contaminate the output.
                continue;
            }

            yield return new FormatResultRecord(path, changed, Formatted: true);
        }

        if (options.CheckOnly && anyChanged)
        {
            // Communicate non-zero exit via a non-throwing diagnostic so
            // pipelines can still inspect the per-file records.
            context.Shell().SetLastExitCode(1);
        }
    }

    private static FormatOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var paths = new List<string>();
        var checkOnly = false;
        var stdoutOnly = false;
        var diff = false;
        var stopFlags = false;

        foreach (var raw in arguments)
        {
            if (raw is not string text || text.Length == 0)
            {
                if (raw is not null) paths.Add(raw.ToString()!);
                continue;
            }

            if (!stopFlags && text == "--")
            {
                stopFlags = true;
                continue;
            }

            if (!stopFlags && text.StartsWith("--", StringComparison.Ordinal))
            {
                switch (text)
                {
                    case "--check":
                        checkOnly = true;
                        continue;
                    case "--stdout":
                        stdoutOnly = true;
                        continue;
                    case "--diff":
                        diff = true;
                        continue;
                    default:
                        throw new InvalidOperationException($"format: unknown option '{text}'.");
                }
            }

            paths.Add(text);
        }

        return new FormatOptions(paths, checkOnly, stdoutOnly, diff);
    }

    /// <summary>
    /// Emits a minimal unified-style diff to stderr. Intentionally
    /// rudimentary; the goal is "did anything change and where" rather
    /// than full git-quality output.
    /// </summary>
    private static void EmitUnifiedDiff(string path, string original, string formatted)
    {
        Console.Error.WriteLine($"--- {path}");
        Console.Error.WriteLine($"+++ {path} (formatted)");
        var originalLines = original.Split('\n');
        var formattedLines = formatted.Split('\n');
        var max = Math.Max(originalLines.Length, formattedLines.Length);
        for (var i = 0; i < max; i++)
        {
            var a = i < originalLines.Length ? originalLines[i] : null;
            var b = i < formattedLines.Length ? formattedLines[i] : null;
            if (a == b) continue;
            if (a is not null) Console.Error.WriteLine($"-{a}");
            if (b is not null) Console.Error.WriteLine($"+{b}");
        }
    }

    private sealed record FormatOptions(
        IReadOnlyList<string> Paths,
        bool CheckOnly,
        bool StdoutOnly,
        bool Diff);

    public sealed record FormatResultRecord(string Path, bool Changed, bool Formatted);
}

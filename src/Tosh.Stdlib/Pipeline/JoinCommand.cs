using System.Globalization;
using System.Text;

using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("separator-or-segment", "In default mode: the string placed between items. In path mode (-p/--path): an additional path segment appended after any piped input.", Required = false, TypeName = "string")]
[CommandOption("-p", "Path mode: join segments with the platform path separator using System.IO.Path.Join semantics. Piped input is prepended; positional args are additional segments.")]
[CommandOption("--path", "Alias for -p.")]
[CommandExample("echo a b c | join \"-\"", Title = "Join scalar items with '-'")]
[CommandExample("[1, 2, 3] | join \", \"", Title = "Stringify and join array elements")]
[CommandExample("cat words.txt | lines | join \" \"", Title = "Collapse lines into a single space-separated string")]
[CommandExample("join -p \"etc\" \"tosh\" \"config.toml\"", Title = "Build a path from arguments")]
[CommandExample("pwd | join -p \"logs\" \"today.log\"", Title = "Build a path from piped input plus more segments")]
[CommandOutput("Default mode: a single string of pipeline items separated by the given separator. Path mode: a single path string.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, AcceptsList = true, AcceptsTable = true,
    Description = "Consumes every pipeline item, converts it to a string, and joins them. In path mode, piped items become the leading path segments.")]
public sealed class JoinCommand : ShellCommand
{
    public JoinCommand()
        : base("join", "Joins pipeline items into a single string, or combines path segments with -p/--path.", "... | join [-p|--path] [separator-or-segment ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var pathMode = parsed.HasFlag("p", "path");

        if (pathMode)
        {
            var segments = new List<string>();

            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                foreach (var element in ShellIterationUtilities.ExpandIterationItems(item))
                {
                    segments.Add(FormatItem(element));
                }
            }

            foreach (var arg in parsed.Positionals)
            {
                segments.Add(FormatItem(arg));
            }

            yield return System.IO.Path.Join(segments.ToArray());
            yield break;
        }

        var separator = parsed.Positionals.Count > 0
            ? FormatSeparator(parsed.Positionals[0])
            : string.Empty;

        var builder = new StringBuilder();
        var first = true;

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            foreach (var element in ShellIterationUtilities.ExpandIterationItems(item))
            {
                if (!first) builder.Append(separator);
                builder.Append(FormatItem(element));
                first = false;
            }
        }

        yield return builder.ToString();
    }

    private static string FormatSeparator(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        _ => FormatItem(value),
    };

    private static string FormatItem(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

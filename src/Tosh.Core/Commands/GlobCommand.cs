namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("pattern", "One or more glob patterns to expand.")]
[CommandOption("-a", "Include hidden entries in results.")]
[CommandExample("glob *.txt")]
[CommandExample("glob -a **/*.cs", Title = "Recursive with hidden files")]
[CommandOutput("Returns FileSystemEntry objects for each matched path.", TypeName = "FileSystemEntry", Members = "Name, FullName, Type, Size")]
[CommandSideEffects(ReadsFiles = true)]
[PipelineInput(AcceptsList = true, Description = "Uses piped patterns when no arguments are given.")]
public sealed class GlobCommand : ShellCommand
{
    public GlobCommand()
        : base("glob", "Expands filesystem glob patterns.", "glob [-a] <pattern> [pattern ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var includeHidden = parsed.HasFlag("a", "all");
        var patterns = new List<string>();

        if (parsed.Positionals.Count > 0)
        {
            patterns.AddRange(parsed.Positionals.Select(ResolvePattern));
        }
        else
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                patterns.Add(ResolvePattern(item));
            }
        }

        if (patterns.Count == 0)
        {
            throw new InvalidOperationException("glob requires at least one pattern or piped pattern input.");
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var matches = PathUtilities.ExpandGlob(context.Runtime.CurrentDirectory, pattern, includeHidden);

            foreach (var match in matches)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(match.FullPath))
                {
                    yield return FileSystemEntry.From(new FileInfo(match.FullPath));
                }
                else if (Directory.Exists(match.FullPath))
                {
                    yield return FileSystemEntry.From(new DirectoryInfo(match.FullPath));
                }
            }
        }
    }

    private static string ResolvePattern(object? value)
    {
        return value switch
        {
            string text => text,
            FileSystemInfo fileSystemInfo => fileSystemInfo.FullName,
            FileSystemEntry entry => entry.FullName,
            _ => CommandArguments.RequireString([value], 0, "pattern"),
        };
    }
}

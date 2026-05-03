using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path", "One or more paths to extract the filename from.", TypeName = "path-like")]
[CommandArgument("suffix", "Optional suffix to strip from the filename.", Required = false)]
[CommandExample("basename /home/user/file.txt")]
[CommandExample("basename file.tar.gz .tar.gz", Title = "Strip suffix")]
[CommandOutput("Returns the filename component of each path.", ClrType = typeof(IAsyncEnumerable<string>))]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Accepts piped path-like values.")]
public sealed class BaseNameCommand : ShellCommand
{
    public BaseNameCommand()
        : base("basename", "Returns the file name portion of each path.", "basename <path> [suffix]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        IReadOnlyList<object?> values;
        string? suffix = null;

        if (context.Arguments.Count > 0)
        {
            values = context.Arguments.Count > 1
                ? [context.Arguments[0]]
                : context.Arguments;

            if (context.Arguments.Count > 1)
            {
                suffix = context.Arguments[1]?.ToString();
            }
        }
        else
        {
            values = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        }

        if (values.Count == 0)
        {
            throw new InvalidOperationException("basename requires at least one path or pipeline input.");
        }

        foreach (var value in values)
        {
            var path = value switch
            {
                FileSystemEntry entry => entry.FullName,
                FileSystemInfo info => info.FullName,
                _ => value?.ToString(),
            };

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var name = GetBaseName(path);

            if (!string.IsNullOrEmpty(suffix) &&
                name.EndsWith(suffix, StringComparison.Ordinal) &&
                name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
            }

            yield return name;
        }
    }

    private static string GetBaseName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (trimmed.Length == 0)
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        return Path.GetFileName(trimmed);
    }
}

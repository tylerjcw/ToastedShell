using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path", "One or more paths to extract the directory from.", TypeName = "path-like")]
[CommandExample("dirname /home/user/file.txt")]
[CommandOutput("Returns the directory component of each path.")]
[PipelineInput(AcceptsScalar = true, AcceptsList = true, Description = "Accepts piped path-like values.")]
public sealed class DirNameCommand : ShellCommand
{
    public DirNameCommand()
        : base("dirname", "Returns the directory portion of each path.", "dirname <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = context.Arguments.Count > 0
            ? context.Arguments
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            throw new InvalidOperationException("dirname requires at least one path or pipeline input.");
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

            yield return GetDirectoryName(path);
        }
    }

    private static string GetDirectoryName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (trimmed.Length == 0)
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        if (Path.GetPathRoot(trimmed)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) == trimmed)
        {
            return trimmed;
        }

        return Path.GetDirectoryName(trimmed) ?? ".";
    }
}

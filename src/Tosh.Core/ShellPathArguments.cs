namespace Tosh.Core;

public static class ShellPathArguments
{
    public static async Task<IReadOnlyList<string>> CollectAsync(
        CommandContext context,
        IReadOnlyList<object?> positionals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(positionals);

        if (positionals.Count > 0)
        {
            return ExpandMany(context.Runtime.CurrentDirectory, positionals);
        }

        var paths = new List<string>();

        await foreach (var item in context.Input.WithCancellation(cancellationToken))
        {
            paths.AddRange(Expand(context.Runtime.CurrentDirectory, item));
        }

        return paths;
    }

    public static IReadOnlyList<string> ExpandMany(string currentDirectory, IReadOnlyList<object?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentNullException.ThrowIfNull(values);

        var paths = new List<string>();

        foreach (var value in values)
        {
            paths.AddRange(Expand(currentDirectory, value));
        }

        return paths;
    }

    public static IReadOnlyList<string> Expand(string currentDirectory, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        return value switch
        {
            string text => ExpandString(currentDirectory, text),
            FileSystemInfo fileSystemInfo => [fileSystemInfo.FullName],
            FileSystemEntry entry => [entry.FullName],
            _ => throw new InvalidOperationException($"Value of type '{value?.GetType().FullName ?? "null"}' cannot be used as a path."),
        };
    }

    public static string Resolve(string currentDirectory, object? value)
    {
        return value switch
        {
            string text => PathUtilities.ResolvePath(currentDirectory, text),
            FileSystemInfo fileSystemInfo => fileSystemInfo.FullName,
            FileSystemEntry entry => entry.FullName,
            _ => throw new InvalidOperationException($"Value of type '{value?.GetType().FullName ?? "null"}' cannot be used as a path."),
        };
    }

    private static IReadOnlyList<string> ExpandString(string currentDirectory, string text)
    {
        if (PathUtilities.ContainsGlobPattern(text))
        {
            var matches = PathUtilities.ExpandGlob(currentDirectory, text);

            if (matches.Count > 0)
            {
                return matches.Select(static match => match.FullPath).ToArray();
            }
        }

        return [PathUtilities.ResolvePath(currentDirectory, text)];
    }
}

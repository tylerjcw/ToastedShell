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
            return positionals.Select(argument => Resolve(context.Runtime.CurrentDirectory, argument)).ToArray();
        }

        var paths = new List<string>();

        await foreach (var item in context.Input.WithCancellation(cancellationToken))
        {
            paths.Add(Resolve(context.Runtime.CurrentDirectory, item));
        }

        return paths;
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
}

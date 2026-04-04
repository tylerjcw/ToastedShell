using System.IO;
using System.Text;

namespace Tosh.Core;

internal static class StreamCommandUtilities
{
    public const int DefaultReadChunkSize = 4096;

    public static async Task<(ManagedFileHandle Handle, IReadOnlyList<object?> RemainingArguments)> ResolveSingleHandleAndArgumentsAsync(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Arguments.Count > 0 && context.Arguments[0] is ManagedFileHandle explicitHandle)
        {
            return (explicitHandle, CommandArguments.Slice(context.Arguments, 1));
        }

        var pipedValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (pipedValues.Count == 0 || pipedValues[0] is not ManagedFileHandle pipedHandle)
        {
            throw new InvalidOperationException("This command expects a file handle from the pipeline or as its first argument.");
        }

        return (pipedHandle, context.Arguments);
    }

    public static async Task<(ManagedFileHandle Handle, int? Count)> ResolveSingleReadableHandleAsync(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Arguments.Count > 0 && context.Arguments[0] is ManagedFileHandle explicitHandle)
        {
            int? count = context.Arguments.Count > 1
                ? CommandArguments.RequireConverted<int>(context.Arguments, 1, "count")
                : null;
            return (explicitHandle, count);
        }

        var pipedValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (pipedValues.Count == 0 || pipedValues[0] is not ManagedFileHandle pipedHandle)
        {
            throw new InvalidOperationException("This command expects a file handle from the pipeline or as its first argument.");
        }

        int? pipedCount = context.Arguments.Count > 0
            ? CommandArguments.RequireConverted<int>(context.Arguments, 0, "count")
            : null;

        return (pipedHandle, pipedCount);
    }

    public static async Task<IReadOnlyList<ManagedFileHandle>> ResolveHandleListAsync(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Arguments.Count > 0)
        {
            return context.Arguments.Select(ResolveHandle).ToArray();
        }

        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (input.Count == 0)
        {
            throw new InvalidOperationException("This command expects one or more file handles.");
        }

        return input.Select(ResolveHandle).ToArray();
    }

    public static ManagedFileHandle ResolveHandle(object? value)
    {
        return value as ManagedFileHandle
               ?? throw new InvalidOperationException("Expected a file handle value.");
    }

    public static SeekOrigin ParseSeekOrigin(object? value)
    {
        var text = value?.ToString();

        return text?.ToLowerInvariant() switch
        {
            null or "" or "begin" or "start" => SeekOrigin.Begin,
            "current" or "cur" => SeekOrigin.Current,
            "end" => SeekOrigin.End,
            _ => throw new InvalidOperationException($"Unknown seek origin '{text}'. Use begin, current, or end."),
        };
    }

    public static Encoding? ResolveEncoding(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(encodingName);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Unknown text encoding '{encodingName}'. {exception.Message}");
        }
    }
}

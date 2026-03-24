namespace Tosh.Core;

public static class StructuredTextInput
{
    public static async Task<string> ReadAllTextAsync(
        CommandContext context,
        IReadOnlyList<object?>? explicitValues = null,
        string? missingInputMessage = null)
    {
        var items = await ReadItemsAsync(context, explicitValues, missingInputMessage);
        return string.Join(Environment.NewLine, items);
    }

    public static async Task<IReadOnlyList<string>> ReadItemsAsync(
        CommandContext context,
        IReadOnlyList<object?>? explicitValues = null,
        string? missingInputMessage = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (explicitValues is { Count: > 0 })
        {
            return explicitValues
                .Select(ExternalTextSerializer.Serialize)
                .ToArray();
        }

        var inputValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (inputValues.Count == 0)
        {
            throw new InvalidOperationException(missingInputMessage ?? "This command expects text input.");
        }

        return inputValues
            .Select(ExternalTextSerializer.Serialize)
            .ToArray();
    }
}

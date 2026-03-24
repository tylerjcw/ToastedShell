namespace Tosh.Core;

public static class CommandArguments
{
    public static string RequireString(IReadOnlyList<object?> arguments, int index, string label)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Missing required argument: {label}.");
        }

        if (arguments[index] is string text)
        {
            return text;
        }

        throw new InvalidOperationException($"Argument '{label}' must be a bareword or string literal.");
    }

    public static IReadOnlyList<object?> Slice(IReadOnlyList<object?> arguments, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (startIndex >= arguments.Count)
        {
            return Array.Empty<object?>();
        }

        var result = new object?[arguments.Count - startIndex];

        for (var index = startIndex; index < arguments.Count; index++)
        {
            result[index - startIndex] = arguments[index];
        }

        return result;
    }

    public static T RequireConverted<T>(IReadOnlyList<object?> arguments, int index, string label)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Missing required argument: {label}.");
        }

        if (TypeConversion.TryConvert(arguments[index], typeof(T), out var converted) && converted is T value)
        {
            return value;
        }

        throw new InvalidOperationException($"Argument '{label}' could not be converted to {typeof(T).Name}.");
    }
}

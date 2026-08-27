namespace Tosh.Runtime;

public static class CommandArguments
{
    public readonly record struct ParsedTypeName(string TypeName, int ConsumedArgumentCount);

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

    public static string RequireTypeName(IReadOnlyList<object?> arguments, int index, string label)
    {
        return RequireParsedTypeName(arguments, index, label).TypeName;
    }

    public static ParsedTypeName RequireParsedTypeName(IReadOnlyList<object?> arguments, int index, string label)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Missing required argument: {label}.");
        }

        var baseTypeName = arguments[index] switch
        {
            string text => text,
            Type type => ReflectionMetadataUtilities.GetDisplayName(type),
            IShellTypeDescriptor descriptor => descriptor.ShellTypeName,
            IShellTypedObject typed => typed.ShellTypeDescriptor.ShellTypeName,
            _ => throw new InvalidOperationException($"Argument '{label}' must be a type name or CLR type value."),
        };

        if (index + 1 >= arguments.Count ||
            arguments[index + 1] is not string nextToken ||
            !nextToken.Contains('<', StringComparison.Ordinal))
        {
            return new ParsedTypeName(baseTypeName, 1);
        }

        var builder = new System.Text.StringBuilder();
        var depth = 0;
        var consumedCount = 1;

        for (var currentIndex = index + 1; currentIndex < arguments.Count; currentIndex++)
        {
            if (arguments[currentIndex] is not string token)
            {
                break;
            }

            if (consumedCount == 1 && !token.Contains('<', StringComparison.Ordinal))
            {
                break;
            }

            builder.Append(token);
            depth += token.Count(character => character == '<');
            depth -= token.Count(character => character == '>');
            consumedCount++;

            if (depth <= 0 && builder.ToString().Contains('>', StringComparison.Ordinal))
            {
                return new ParsedTypeName(baseTypeName + builder, consumedCount);
            }
        }

        return new ParsedTypeName(baseTypeName, 1);
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

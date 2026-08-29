namespace Tosh.Runtime;

/// <summary>
/// Shell-provided static methods shared by every CLR enum type.
/// </summary>
internal static class EnumStaticSurface
{
    private static readonly string[] MethodNames = ["names", "values"];

    public static IReadOnlyList<string> Names => MethodNames;

    public static bool TryInvoke(
        Type type,
        string methodName,
        IReadOnlyList<object?> arguments,
        out InvocationResult result)
    {
        result = null!;

        if (!type.IsEnum ||
            methodName is null ||
            !MethodNames.Contains(methodName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (arguments.Count != 0)
        {
            throw new InvalidOperationException(
                $"Enum method '{type.FullName ?? type.Name}.{methodName}' expects no arguments.");
        }

        var value = string.Equals(methodName, "values", StringComparison.OrdinalIgnoreCase)
            ? GetUnderlyingValues(type)
            : Enum.GetNames(type);

        result = new InvocationResult(value, ReturnedVoid: false);
        return true;
    }

    public static ShellMethodDescriptor Describe(Type type, string methodName)
    {
        var values = string.Equals(methodName, "values", StringComparison.OrdinalIgnoreCase);
        var returnType = values
            ? $"{ReflectionMetadataUtilities.GetDisplayName(Enum.GetUnderlyingType(type))}[]"
            : "System.String[]";

        return new ShellMethodDescriptor(
            methodName,
            ReturnTypeName: returnType,
            IsStatic: true,
            ParameterCount: 0,
            Signature: $"static {returnType} {methodName}()",
            IsHidden: false);
    }

    private static Array GetUnderlyingValues(Type enumType)
    {
        var enumValues = Enum.GetValues(enumType);
        var underlyingType = Enum.GetUnderlyingType(enumType);
        var values = Array.CreateInstance(underlyingType, enumValues.Length);

        for (var index = 0; index < enumValues.Length; index++)
        {
            var enumValue = (Enum)enumValues.GetValue(index)!;
            values.SetValue(ReflectionMetadataUtilities.GetEnumNumericValue(enumValue), index);
        }

        return values;
    }
}

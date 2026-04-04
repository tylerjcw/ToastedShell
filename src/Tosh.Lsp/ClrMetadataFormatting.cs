using System.Reflection;

namespace Tosh.Lsp;

internal static class ClrMetadataFormatting
{
    public static string FormatTypeDisplayName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var genericDefinitionName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tickIndex = genericDefinitionName.IndexOf('`');

        if (tickIndex >= 0)
        {
            genericDefinitionName = genericDefinitionName[..tickIndex];
        }

        return $"{genericDefinitionName}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeDisplayName))}>";
    }

    public static string FormatMethodSignature(MethodInfo method)
    {
        return $"{(method.IsStatic ? "static " : string.Empty)}{FormatTypeDisplayName(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(FormatParameter))})";
    }

    public static string FormatConstructorSignature(ConstructorInfo constructor)
    {
        var typeName = constructor.DeclaringType is null ? ".ctor" : FormatTypeDisplayName(constructor.DeclaringType);
        return $"{typeName}({string.Join(", ", constructor.GetParameters().Select(FormatParameter))})";
    }

    public static string FormatParameter(ParameterInfo parameter)
    {
        var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
        return $"{prefix}{FormatTypeDisplayName(UnwrapByRef(parameter.ParameterType))} {parameter.Name}";
    }

    private static Type UnwrapByRef(Type type) => type.IsByRef ? type.GetElementType() ?? type : type;
}

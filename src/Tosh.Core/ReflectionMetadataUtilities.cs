using System.Reflection;

namespace Tosh.Core;

internal static class ReflectionMetadataUtilities
{
    public static IReadOnlyList<Type> ResolveTypes(CommandContext context, IReadOnlyList<object?> arguments, bool allowInput = true)
    {
        var types = new List<Type>();

        if (arguments.Count > 0)
        {
            foreach (var argument in arguments)
            {
                types.Add(ResolveType(context, argument));
            }
        }
        else if (allowInput)
        {
            throw new InvalidOperationException("This command expects one or more type names or pipeline objects.");
        }

        return types
            .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static Type ResolveType(CommandContext context, object? value)
    {
        return value switch
        {
            null => throw new InvalidOperationException("A null value cannot be resolved to a type."),
            Type type => type,
            string text => context.Runtime.TypeResolver.Resolve(text)
                ?? throw new InvalidOperationException($"Unable to resolve type '{text}'."),
            _ => value.GetType(),
        };
    }

    public static string GetDisplayName(Type type)
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

        return $"{genericDefinitionName}<{string.Join(", ", type.GetGenericArguments().Select(GetDisplayName))}>";
    }

    public static string FormatParameters(IEnumerable<ParameterInfo> parameters)
    {
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
                return $"{prefix}{GetDisplayName(UnwrapByRef(parameter.ParameterType))} {parameter.Name}";
            }));
    }

    public static string FormatMethodSignature(MethodInfo method)
    {
        var prefix = method.IsStatic ? "static " : string.Empty;
        return $"{prefix}{GetDisplayName(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})";
    }

    public static string FormatConstructorSignature(ConstructorInfo constructor)
    {
        var typeName = constructor.DeclaringType is null ? ".ctor" : GetDisplayName(constructor.DeclaringType);
        return $"{typeName}({FormatParameters(constructor.GetParameters())})";
    }

    public static ProjectedObject CreateTypeProjection(Type type)
    {
        return new ProjectedObject(
        [
            new ProjectedField("Name", "Name", type.Name),
            new ProjectedField("FullName", "FullName", type.FullName ?? type.Name),
            new ProjectedField("Namespace", "Namespace", type.Namespace),
            new ProjectedField("Assembly", "Assembly", type.Assembly.GetName().Name),
            new ProjectedField("BaseType", "BaseType", type.BaseType is null ? null : GetDisplayName(type.BaseType)),
            new ProjectedField("IsClass", "IsClass", type.IsClass),
            new ProjectedField("IsInterface", "IsInterface", type.IsInterface),
            new ProjectedField("IsEnum", "IsEnum", type.IsEnum),
            new ProjectedField("IsValueType", "IsValueType", type.IsValueType),
            new ProjectedField("IsAbstract", "IsAbstract", type.IsAbstract),
            new ProjectedField("IsGenericType", "IsGenericType", type.IsGenericType),
            new ProjectedField("IsArray", "IsArray", type.IsArray),
            new ProjectedField("IsPublic", "IsPublic", type.IsPublic || type.IsNestedPublic),
        ]);
    }

    private static Type UnwrapByRef(Type type)
    {
        return type.IsByRef ? type.GetElementType() ?? type : type;
    }
}

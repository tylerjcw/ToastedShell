using System.Reflection;
using System.Globalization;

namespace Tosh.Runtime;

internal static class ReflectionMetadataUtilities
{
    public static IReadOnlyList<object> ResolveTypeLikeTargets(CommandContext context, IReadOnlyList<object?> arguments, bool allowInput = true)
    {
        var types = new List<object>();

        if (arguments.Count > 0)
        {
            foreach (var argument in arguments)
            {
                types.Add(ResolveTypeLikeTarget(context, argument));
            }
        }
        else if (allowInput)
        {
            throw new InvalidOperationException("This command expects one or more type names or pipeline objects.");
        }

        return types
            .DistinctBy(GetTargetIdentity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
            IShellTypeDescriptor descriptor => throw new InvalidOperationException($"'{descriptor.ShellTypeName}' is a ToSh class, not a CLR type."),
            IShellTypedObject typed => throw new InvalidOperationException($"'{typed.ShellTypeDescriptor.ShellTypeName}' is a ToSh class, not a CLR type."),
            string text => context.TypeResolver.Resolve(text)
                ?? throw new InvalidOperationException($"Unable to resolve type '{text}'."),
            _ => value.GetType(),
        };
    }

    public static object ResolveTypeLikeTarget(CommandContext context, object? value)
    {
        return value switch
        {
            null => throw new InvalidOperationException("A null value cannot be resolved to a type."),
            IShellTypeDescriptor descriptor => descriptor,
            IShellTypedObject typed => typed.ShellTypeDescriptor,
            _ when BuiltInShellTypes.TryDescribeRuntimeValue(value, out var builtInDescriptor) => builtInDescriptor,
            Type type => type,
            string text => TryResolveShellType(context.Runtime, text, out var shellDescriptor)
                ? shellDescriptor
                : context.TypeResolver.Resolve(text)
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

    public static object GetEnumNumericValue(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var runtimeType = value.GetType();
        var underlyingType = Enum.GetUnderlyingType(runtimeType);
        return Convert.ChangeType(value, underlyingType, CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> GetEnumNames(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = value.ToString();

        if (string.IsNullOrWhiteSpace(text) ||
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return Array.Empty<string>();
        }

        return text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    public static string FormatEnumValue(Enum value, bool includeTypeName)
    {
        ArgumentNullException.ThrowIfNull(value);

        var typeName = GetDisplayName(value.GetType());
        var names = GetEnumNames(value);

        if (names.Count == 0)
        {
            var numeric = GetEnumNumericValue(value);
            return $"{typeName}({Convert.ToString(numeric, CultureInfo.InvariantCulture)})";
        }

        if (!includeTypeName)
        {
            return string.Join(" | ", names);
        }

        return string.Join(" | ", names.Select(name => $"{typeName}.{name}"));
    }

    public static IReadOnlyList<ShellConstructorDescriptor> GetConstructorDescriptors(object typeTarget)
    {
        if (typeTarget is Type type)
        {
            var descriptors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .OrderBy(constructor => constructor.GetParameters().Length)
                .ThenBy(constructor => FormatConstructorSignature(constructor), StringComparer.OrdinalIgnoreCase)
                .Select(constructor => new ShellConstructorDescriptor(
                    constructor.GetParameters().Length,
                    FormatConstructorSignature(constructor)))
                .ToList();

            if (descriptors.Count == 0 && type.IsValueType && !type.IsEnum)
            {
                descriptors.Add(new ShellConstructorDescriptor(0, $"{GetDisplayName(type)}()"));
            }

            return descriptors;
        }

        if (typeTarget is IShellTypeDescriptor descriptor)
        {
            return descriptor.GetShellConstructors()
                .OrderBy(constructor => constructor.ParameterCount)
                .ThenBy(constructor => constructor.Signature, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        throw new InvalidOperationException("Unsupported type target.");
    }

    public static System.Dynamic.ExpandoObject CreateTypeProjection(Type type)
    {
        return ShellRecordUtilities.CreateExpando(
        [
            new KeyValuePair<string, object?>("Name", type.Name),
            new KeyValuePair<string, object?>("FullName", type.FullName ?? type.Name),
            new KeyValuePair<string, object?>("Namespace", type.Namespace),
            new KeyValuePair<string, object?>("Assembly", type.Assembly.GetName().Name),
            new KeyValuePair<string, object?>("BaseType", type.BaseType is null ? null : GetDisplayName(type.BaseType)),
            new KeyValuePair<string, object?>("IsClass", type.IsClass),
            new KeyValuePair<string, object?>("IsInterface", type.IsInterface),
            new KeyValuePair<string, object?>("IsEnum", type.IsEnum),
            new KeyValuePair<string, object?>("IsValueType", type.IsValueType),
            new KeyValuePair<string, object?>("IsAbstract", type.IsAbstract),
            new KeyValuePair<string, object?>("IsGenericType", type.IsGenericType),
            new KeyValuePair<string, object?>("IsArray", type.IsArray),
            new KeyValuePair<string, object?>("IsPublic", type.IsPublic || type.IsNestedPublic),
        ]);
    }

    public static System.Dynamic.ExpandoObject CreateTypeProjection(object typeTarget)
    {
        return typeTarget switch
        {
            Type type => CreateTypeProjection(type),
            IShellTypeDescriptor descriptor => ShellRecordUtilities.CreateExpando(
            [
                new KeyValuePair<string, object?>("Name", descriptor.ShellTypeName),
                new KeyValuePair<string, object?>("FullName", descriptor.ShellFullName),
                new KeyValuePair<string, object?>("Namespace", descriptor.ShellNamespace),
                new KeyValuePair<string, object?>("Assembly", descriptor.ShellAssemblyName),
                new KeyValuePair<string, object?>("BaseType", descriptor.ShellBaseTypeName),
                new KeyValuePair<string, object?>("IsClass", descriptor.ShellIsClass),
                new KeyValuePair<string, object?>("IsInterface", descriptor.ShellIsInterface),
                new KeyValuePair<string, object?>("IsEnum", descriptor.ShellIsEnum),
                new KeyValuePair<string, object?>("IsValueType", descriptor.ShellIsValueType),
                new KeyValuePair<string, object?>("IsAbstract", descriptor.ShellIsAbstract),
                new KeyValuePair<string, object?>("IsGenericType", descriptor.ShellIsGenericType),
                new KeyValuePair<string, object?>("IsArray", descriptor.ShellIsArray),
                new KeyValuePair<string, object?>("IsPublic", descriptor.ShellIsPublic),
            ]),
            _ => throw new InvalidOperationException("Unsupported type target."),
        };
    }

    public static IEnumerable<System.Dynamic.ExpandoObject> EnumerateMemberProjections(object typeTarget, bool includeHidden = false)
    {
        if (typeTarget is Type type)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(property => property.GetIndexParameters().Length == 0))
            {
                yield return ShellRecordUtilities.CreateExpando(
                [
                    new KeyValuePair<string, object?>("Type", GetDisplayName(type)),
                    new KeyValuePair<string, object?>("Name", property.Name),
                    new KeyValuePair<string, object?>("Kind", "Property"),
                    new KeyValuePair<string, object?>("Origin", "CLR"),
                    new KeyValuePair<string, object?>("MemberType", GetDisplayName(property.PropertyType)),
                    new KeyValuePair<string, object?>("Static", (property.GetMethod ?? property.SetMethod)?.IsStatic ?? false),
                    new KeyValuePair<string, object?>("Writable", property.CanWrite),
                ]);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                yield return ShellRecordUtilities.CreateExpando(
                [
                    new KeyValuePair<string, object?>("Type", GetDisplayName(type)),
                    new KeyValuePair<string, object?>("Name", field.Name),
                    new KeyValuePair<string, object?>("Kind", "Field"),
                    new KeyValuePair<string, object?>("Origin", "CLR"),
                    new KeyValuePair<string, object?>("MemberType", GetDisplayName(field.FieldType)),
                    new KeyValuePair<string, object?>("Static", field.IsStatic),
                    new KeyValuePair<string, object?>("Writable", !(field.IsInitOnly || field.IsLiteral)),
                ]);
            }

            if (type.IsEnum)
            {
                var underlyingType = Enum.GetUnderlyingType(type);

                yield return ShellRecordUtilities.CreateExpando(
                [
                    new KeyValuePair<string, object?>("Type", GetDisplayName(type)),
                    new KeyValuePair<string, object?>("Name", "NumericValue"),
                    new KeyValuePair<string, object?>("Kind", "Helper"),
                    new KeyValuePair<string, object?>("Origin", "Shell"),
                    new KeyValuePair<string, object?>("MemberType", GetDisplayName(underlyingType)),
                    new KeyValuePair<string, object?>("Static", false),
                    new KeyValuePair<string, object?>("Writable", false),
                ]);

                yield return ShellRecordUtilities.CreateExpando(
                [
                    new KeyValuePair<string, object?>("Type", GetDisplayName(type)),
                    new KeyValuePair<string, object?>("Name", "Names"),
                    new KeyValuePair<string, object?>("Kind", "Helper"),
                    new KeyValuePair<string, object?>("Origin", "Shell"),
                    new KeyValuePair<string, object?>("MemberType", "System.String[]"),
                    new KeyValuePair<string, object?>("Static", false),
                    new KeyValuePair<string, object?>("Writable", false),
                ]);
            }

            yield break;
        }

        if (typeTarget is IShellTypeDescriptor descriptor)
        {
            foreach (var member in descriptor.GetShellMembers(includeHidden))
            {
                yield return ShellRecordUtilities.CreateExpando(
                [
                    new KeyValuePair<string, object?>("Type", descriptor.ShellTypeName),
                    new KeyValuePair<string, object?>("Name", member.Name),
                    new KeyValuePair<string, object?>("Kind", member.Kind),
                    new KeyValuePair<string, object?>("Origin", "ToSh"),
                    new KeyValuePair<string, object?>("MemberType", member.TypeName),
                    new KeyValuePair<string, object?>("Static", member.IsStatic),
                    new KeyValuePair<string, object?>("Writable", member.IsWritable),
                    new KeyValuePair<string, object?>("Hidden", member.IsHidden),
                ]);
            }

            yield break;
        }

        throw new InvalidOperationException("Unsupported type target.");
    }

    public static IEnumerable<System.Dynamic.ExpandoObject> EnumerateMethodProjections(object typeTarget, bool includeHidden = false)
    {
        if (typeTarget is Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(method => !method.IsSpecialName)
                         .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return ShellRecordUtilities.CreateExpando(
                [
                    new KeyValuePair<string, object?>("Type", GetDisplayName(type)),
                    new KeyValuePair<string, object?>("Name", method.Name),
                    new KeyValuePair<string, object?>("ReturnType", GetDisplayName(method.ReturnType)),
                    new KeyValuePair<string, object?>("Static", method.IsStatic),
                    new KeyValuePair<string, object?>("ParameterCount", method.GetParameters().Length),
                    new KeyValuePair<string, object?>("Signature", FormatMethodSignature(method)),
                ]);
            }

            yield break;
        }

        if (typeTarget is IShellTypeDescriptor descriptor)
        {
            foreach (var method in descriptor.GetShellMethods(includeHidden))
            {
                yield return ShellRecordUtilities.CreateExpando(
                [
                    new KeyValuePair<string, object?>("Type", descriptor.ShellTypeName),
                    new KeyValuePair<string, object?>("Name", method.Name),
                    new KeyValuePair<string, object?>("ReturnType", method.ReturnTypeName),
                    new KeyValuePair<string, object?>("Static", method.IsStatic),
                    new KeyValuePair<string, object?>("ParameterCount", method.ParameterCount),
                    new KeyValuePair<string, object?>("Signature", method.Signature),
                    new KeyValuePair<string, object?>("Hidden", method.IsHidden),
                ]);
            }

            yield break;
        }

        throw new InvalidOperationException("Unsupported type target.");
    }

    public static IEnumerable<System.Dynamic.ExpandoObject> EnumerateConstructorProjections(object typeTarget)
    {
        var typeName = typeTarget switch
        {
            Type type => GetDisplayName(type),
            IShellTypeDescriptor descriptor => descriptor.ShellTypeName,
            _ => throw new InvalidOperationException("Unsupported type target."),
        };

        foreach (var constructor in GetConstructorDescriptors(typeTarget))
        {
            yield return ShellRecordUtilities.CreateExpando(
            [
                new KeyValuePair<string, object?>("Type", typeName),
                new KeyValuePair<string, object?>("ParameterCount", constructor.ParameterCount),
                new KeyValuePair<string, object?>("Signature", constructor.Signature),
            ]);
        }
    }

    private static bool TryResolveShellType(ToshRuntime runtime, string name, out IShellTypeDescriptor descriptor)
    {
        if (runtime.Classes.TryGetValue(name, out var rawDescriptor) &&
            rawDescriptor is IShellTypeDescriptor shellDescriptor)
        {
            descriptor = shellDescriptor;
            return true;
        }

        if (BuiltInShellTypes.TryResolveStaticType(name, runtime.TypeResolver, out var builtInType) &&
            builtInType is IShellTypeDescriptor builtInDescriptor)
        {
            descriptor = builtInDescriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    private static string GetTargetIdentity(object typeTarget)
    {
        return typeTarget switch
        {
            Type type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
            IShellTypeDescriptor descriptor => descriptor.ShellFullName,
            _ => typeTarget.GetType().FullName ?? typeTarget.GetType().Name,
        };
    }

    private static Type UnwrapByRef(Type type)
    {
        return type.IsByRef ? type.GetElementType() ?? type : type;
    }
}

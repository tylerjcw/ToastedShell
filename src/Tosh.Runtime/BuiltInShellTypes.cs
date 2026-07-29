using System.Collections;
using System.Collections.Concurrent;
using System.Dynamic;
using System.Numerics;
using System.Reflection;

namespace Tosh.Runtime;

public static class BuiltInShellTypes
{
    internal static readonly BuiltInShellTypeDefinition List = new(
        "list",
        typeof(List<object?>),
        CreateList,
        [new ShellConstructorDescriptor(-1, "list(items...)")]);

    internal static readonly BuiltInShellTypeDefinition Array = new(
        "array",
        typeof(object[]),
        CreateArray,
        [new ShellConstructorDescriptor(-1, "array(items...)")],
        isArray: true);

    internal static readonly BuiltInShellTypeDefinition Dict = new(
        "dict",
        typeof(Dictionary<string, object?>),
        CreateDictionary,
        [new ShellConstructorDescriptor(-1, "dict([record] | key, value, ...)")]);

    internal static readonly BuiltInShellTypeDefinition Set = new(
        "set",
        typeof(HashSet<object?>),
        CreateSet,
        [new ShellConstructorDescriptor(-1, "set(items...)")]);

    internal static readonly BuiltInShellTypeDefinition Hashtable = new(
        "hashtable",
        typeof(System.Collections.Hashtable),
        CreateHashtable,
        [new ShellConstructorDescriptor(-1, "hashtable([record] | key, value, ...)")]);

    /// <summary>
    /// The dynamic record type. Named <c>record</c> because that is what the
    /// syntax, the specification, and users call <c>{| … |}</c>; <c>table</c> and
    /// <c>dynamicrecord</c> remain aliases so existing annotations keep working
    /// (<c>TS-P3-11</c>).
    /// </summary>
    internal static readonly BuiltInShellTypeDefinition Table = new(
        "record",
        typeof(ExpandoObject),
        CreateTable,
        [new ShellConstructorDescriptor(-1, "record([record] | key, value, ...)")]);

    internal static readonly BuiltInShellTypeDefinition Tuple = new(
        "tuple",
        typeof(ToshTuple),
        CreateTuple,
        [new ShellConstructorDescriptor(-1, "tuple(items...)")]);

    private static readonly IReadOnlyDictionary<string, BuiltInShellTypeDefinition> Definitions =
        new Dictionary<string, BuiltInShellTypeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["list"] = List,
            ["array"] = Array,
            ["dict"] = Dict,
            ["map"] = Dict,
            ["set"] = Set,
            ["hashtable"] = Hashtable,
            ["record"] = Table,
            ["table"] = Table,
            ["dynamicrecord"] = Table,
            ["tuple"] = Tuple,
        };
    private static readonly ConcurrentDictionary<Type, BuiltInShellTypeDefinition> RuntimeDescriptors = new();

    public static void RegisterDefaults(IDictionary<string, object?> classes)
    {
        foreach (var (name, definition) in Definitions)
        {
            classes[name] = definition;
        }

        classes["Math"] = MathShellType.Instance;
        classes["Vector"] = VectorShellType.Instance;
        classes["vec"] = VectorShellType.Instance;
        classes["Matrix"] = MatrixShellType.Instance;
        classes["matrix"] = MatrixShellType.Instance;
        classes["mat"] = MatrixShellType.Instance;
        classes["Complex"] = ComplexShellType.Instance;
        classes["complex"] = ComplexShellType.Instance;
    }

    public static bool TryResolveStaticType(string name, ITypeResolver resolver, out IShellStaticType definition)
    {
        if (Definitions.TryGetValue(name, out var directDefinition))
        {
            definition = directDefinition;
            return true;
        }

        if (resolver.Resolve(name) is Type resolvedType &&
            TryDescribeRuntimeType(resolvedType, out var descriptor) &&
            descriptor is IShellStaticType staticType)
        {
            definition = staticType;
            return true;
        }

        definition = null!;
        return false;
    }

    /// <summary>
    /// Every name <paramref name="shellTypeName"/>'s type answers to, including
    /// the name itself.
    /// </summary>
    /// <remarks>
    /// Exists so a surface keyed by shell type name — a display profile, for one —
    /// keeps matching after a type is renamed and its old name kept as an alias.
    /// <c>record</c> was <c>table</c> until <c>TS-P3-11</c>, and a user profile
    /// keyed <c>table</c> silently stopped applying until the aliases were
    /// consulted here.
    /// </remarks>
    public static IReadOnlyList<string> AliasesFor(string shellTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellTypeName);

        if (!Definitions.TryGetValue(shellTypeName, out var definition))
        {
            return [shellTypeName];
        }

        return Definitions
            .Where(pair => ReferenceEquals(pair.Value, definition))
            .Select(pair => pair.Key)
            .ToArray();
    }

    public static bool TryDescribeRuntimeValue(object? value, out IShellTypeDescriptor descriptor)
    {
        switch (value)
        {
            case null:
                descriptor = null!;
                return false;

            case ExpandoObject:
                descriptor = Table;
                return true;

            case ToshTuple:
                descriptor = Tuple;
                return true;

            case Dictionary<string, object?>:
                descriptor = Dict;
                return true;

            case System.Collections.Hashtable:
                descriptor = Hashtable;
                return true;

            case ToshVector:
                descriptor = VectorShellType.Instance;
                return true;

            case ToshMatrix:
                descriptor = MatrixShellType.Instance;
                return true;

            case Complex:
                descriptor = ComplexShellType.Instance;
                return true;

            default:
                return TryDescribeRuntimeType(value.GetType(), out descriptor);
        }
    }

    public static bool TryDescribeRuntimeType(Type runtimeType, out IShellTypeDescriptor descriptor)
    {
        if (runtimeType == typeof(List<object?>))
        {
            descriptor = List;
            return true;
        }

        if (runtimeType == typeof(Dictionary<string, object?>))
        {
            descriptor = Dict;
            return true;
        }

        if (runtimeType == typeof(HashSet<object?>))
        {
            descriptor = Set;
            return true;
        }

        if (runtimeType == typeof(object[]))
        {
            descriptor = Array;
            return true;
        }

        if (runtimeType == typeof(ToshVector))
        {
            descriptor = VectorShellType.Instance;
            return true;
        }

        if (runtimeType == typeof(ToshMatrix))
        {
            descriptor = MatrixShellType.Instance;
            return true;
        }

        if (runtimeType == typeof(Complex))
        {
            descriptor = ComplexShellType.Instance;
            return true;
        }

        if (runtimeType.IsArray ||
            (runtimeType.IsGenericType &&
             runtimeType.GetGenericTypeDefinition() is { } genericDefinition &&
             (genericDefinition == typeof(List<>) ||
              genericDefinition == typeof(Dictionary<,>) ||
              genericDefinition == typeof(HashSet<>))))
        {
            descriptor = RuntimeDescriptors.GetOrAdd(runtimeType, CreateRuntimeDescriptor);
            return true;
        }

        descriptor = null!;
        return false;
    }

    private static object CreateList(IReadOnlyList<object?> arguments)
    {
        return CreateList(arguments, explicitElementType: null);
    }

    private static object CreateArray(IReadOnlyList<object?> arguments)
    {
        return CreateArray(arguments, explicitElementType: null);
    }

    private static object CreateSet(IReadOnlyList<object?> arguments)
    {
        return CreateSet(arguments, explicitElementType: null);
    }

    private static object CreateTuple(IReadOnlyList<object?> arguments)
    {
        return new ToshTuple(ExpandEnumerableArgument(arguments));
    }

    private static object CreateTable(IReadOnlyList<object?> arguments)
    {
        if (TryCreateRecordFields(arguments, out var fields))
        {
            return ShellRecordUtilities.CreateExpando(fields);
        }

        throw new InvalidOperationException("table expects no arguments, a single record-like value, or key/value pairs.");
    }

    private static object CreateDictionary(IReadOnlyList<object?> arguments)
    {
        return CreateDictionary(arguments, explicitKeyType: null, explicitValueType: null);
    }

    private static object CreateList(IReadOnlyList<object?> arguments, Type? explicitElementType)
    {
        var items = ExpandEnumerableArgument(arguments);
        var elementType = explicitElementType ?? InferElementType(items);

        if (elementType == typeof(object))
        {
            return items.ToList();
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;

        foreach (var item in items)
        {
            list.Add(ConvertOrThrow(item, elementType, "list"));
        }

        return list;
    }

    private static object CreateArray(IReadOnlyList<object?> arguments, Type? explicitElementType)
    {
        var items = ExpandEnumerableArgument(arguments);
        var elementType = explicitElementType ?? InferElementType(items);

        if (elementType == typeof(object))
        {
            return items.ToArray();
        }

        var array = System.Array.CreateInstance(elementType, items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            array.SetValue(ConvertOrThrow(items[index], elementType, "array"), index);
        }

        return array;
    }

    private static object CreateSet(IReadOnlyList<object?> arguments, Type? explicitElementType)
    {
        var items = ExpandEnumerableArgument(arguments);
        var elementType = explicitElementType ?? InferElementType(items);

        if (elementType == typeof(object))
        {
            return new HashSet<object?>(items);
        }

        var setType = typeof(HashSet<>).MakeGenericType(elementType);
        var set = Activator.CreateInstance(setType)!;
        var addMethod = setType.GetMethod("Add", [elementType])
                        ?? throw new InvalidOperationException($"Could not find Add({elementType.FullName}) on '{setType.FullName}'.");

        foreach (var item in items)
        {
            addMethod.Invoke(set, [ConvertOrThrow(item, elementType, "set")]);
        }

        return set;
    }

    private static object CreateDictionary(IReadOnlyList<object?> arguments, Type? explicitKeyType, Type? explicitValueType)
    {
        if (!TryCreateRecordFields(arguments, out var fields))
        {
            throw new InvalidOperationException("dict expects no arguments, a single record-like value, or key/value pairs.");
        }

        var keyType = explicitKeyType ?? typeof(string);
        var valueType = explicitValueType ?? InferElementType(fields.Select(entry => entry.Value).ToArray());

        if (keyType == typeof(string) && valueType == typeof(object))
        {
            return fields.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        }

        var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var dictionary = (IDictionary)Activator.CreateInstance(dictType)!;

        foreach (var (key, value) in fields)
        {
            dictionary[ConvertOrThrow(key, keyType, "dict")!] = ConvertOrThrow(value, valueType, "dict");
        }

        return dictionary;
    }

    private static object CreateHashtable(IReadOnlyList<object?> arguments)
    {
        if (!TryCreateRecordFields(arguments, out var fields))
        {
            throw new InvalidOperationException("hashtable expects no arguments, a single record-like value, or key/value pairs.");
        }

        var table = new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in fields)
        {
            table[key] = value;
        }

        return table;
    }

    private static IReadOnlyList<object?> ExpandEnumerableArgument(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 1 &&
            arguments[0] is IEnumerable enumerable &&
            arguments[0] is not string &&
            arguments[0] is not IDictionary &&
            arguments[0] is not ExpandoObject)
        {
            return enumerable.Cast<object?>().ToArray();
        }

        return arguments;
    }

    private static Type InferElementType(IReadOnlyList<object?> items)
    {
        Type? elementType = null;

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            var itemType = item.GetType();

            if (elementType is null)
            {
                elementType = itemType;
                continue;
            }

            if (elementType != itemType && !elementType.IsAssignableFrom(itemType))
            {
                return typeof(object);
            }
        }

        return elementType ?? typeof(object);
    }

    private static object? ConvertOrThrow(object? value, Type targetType, string collectionName)
    {
        if (TypeConversion.TryConvert(value, targetType, out var converted))
        {
            return converted;
        }

        throw new InvalidOperationException($"Could not convert '{value}' to '{targetType.FullName}' for {collectionName} construction.");
    }

    private static BuiltInShellTypeDefinition CreateRuntimeDescriptor(Type runtimeType)
    {
        if (runtimeType.IsArray)
        {
            var elementType = runtimeType.GetElementType() ?? typeof(object);
            var displayName = BuildConstructedTypeName("array", [elementType]);
            return new BuiltInShellTypeDefinition(
                displayName,
                runtimeType,
                arguments => CreateArray(arguments, elementType),
                [new ShellConstructorDescriptor(-1, $"{displayName}(items...)")],
                isArray: true);
        }

        if (!runtimeType.IsGenericType)
        {
            throw new InvalidOperationException($"'{runtimeType.FullName}' is not a built-in shell collection type.");
        }

        var genericDefinition = runtimeType.GetGenericTypeDefinition();
        var genericArguments = runtimeType.GetGenericArguments();

        if (genericDefinition == typeof(List<>))
        {
            var displayName = BuildConstructedTypeName("list", genericArguments);
            return new BuiltInShellTypeDefinition(
                displayName,
                runtimeType,
                arguments => CreateList(arguments, genericArguments[0]),
                [new ShellConstructorDescriptor(-1, $"{displayName}(items...)")]);
        }

        if (genericDefinition == typeof(HashSet<>))
        {
            var displayName = BuildConstructedTypeName("set", genericArguments);
            return new BuiltInShellTypeDefinition(
                displayName,
                runtimeType,
                arguments => CreateSet(arguments, genericArguments[0]),
                [new ShellConstructorDescriptor(-1, $"{displayName}(items...)")]);
        }

        if (genericDefinition == typeof(Dictionary<,>))
        {
            var displayName = BuildConstructedTypeName("dict", genericArguments);
            return new BuiltInShellTypeDefinition(
                displayName,
                runtimeType,
                arguments => CreateDictionary(arguments, genericArguments[0], genericArguments[1]),
                [new ShellConstructorDescriptor(-1, $"{displayName}([record] | key, value, ...)")]);
        }

        throw new InvalidOperationException($"'{runtimeType.FullName}' is not a built-in shell collection type.");
    }

    private static string BuildConstructedTypeName(string alias, IReadOnlyList<Type> typeArguments)
    {
        return $"{alias}<{string.Join(", ", typeArguments.Select(GetFriendlyTypeName))}>";
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (type == typeof(bool))
        {
            return "bool";
        }

        if (type == typeof(byte))
        {
            return "byte";
        }

        if (type == typeof(sbyte))
        {
            return "sbyte";
        }

        if (type == typeof(char))
        {
            return "char";
        }

        if (type == typeof(decimal))
        {
            return "decimal";
        }

        if (type == typeof(double))
        {
            return "double";
        }

        if (type == typeof(float))
        {
            return "float";
        }

        if (type == typeof(int))
        {
            return "int";
        }

        if (type == typeof(uint))
        {
            return "uint";
        }

        if (type == typeof(ExpandoObject))
        {
            return "record";
        }

        if (type == typeof(Dictionary<string, object?>))
        {
            return "dict";
        }

        if (type == typeof(System.Collections.Hashtable))
        {
            return "hashtable";
        }

        if (type == typeof(long))
        {
            return "long";
        }

        if (type == typeof(ulong))
        {
            return "ulong";
        }

        if (type == typeof(object))
        {
            return "object";
        }

        if (type == typeof(short))
        {
            return "short";
        }

        if (type == typeof(ushort))
        {
            return "ushort";
        }

        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(ToshTuple))
        {
            return "tuple";
        }

        if (type == typeof(Uri))
        {
            return "uri";
        }

        if (type.IsArray)
        {
            return BuildConstructedTypeName("array", [type.GetElementType() ?? typeof(object)]);
        }

        if (type.IsGenericType)
        {
            var genericDefinition = type.GetGenericTypeDefinition();

            if (genericDefinition == typeof(List<>))
            {
                return BuildConstructedTypeName("list", type.GetGenericArguments());
            }

            if (genericDefinition == typeof(Dictionary<,>))
            {
                return BuildConstructedTypeName("dict", type.GetGenericArguments());
            }

            if (genericDefinition == typeof(HashSet<>))
            {
                return BuildConstructedTypeName("set", type.GetGenericArguments());
            }
        }

        return ReflectionMetadataUtilities.GetDisplayName(type);
    }

    private static bool TryCreateRecordFields(IReadOnlyList<object?> arguments, out IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        if (arguments.Count == 0)
        {
            fields = System.Array.Empty<KeyValuePair<string, object?>>();
            return true;
        }

        if (arguments.Count == 1 &&
            ShellRecordUtilities.TryGetFields(arguments[0], out var recordFields))
        {
            fields = recordFields;
            return true;
        }

        if (arguments.Count % 2 != 0)
        {
            fields = System.Array.Empty<KeyValuePair<string, object?>>();
            return false;
        }

        var pairs = new List<KeyValuePair<string, object?>>(arguments.Count / 2);

        for (var index = 0; index < arguments.Count; index += 2)
        {
            var key = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(key))
            {
                fields = System.Array.Empty<KeyValuePair<string, object?>>();
                return false;
            }

            pairs.Add(new KeyValuePair<string, object?>(key, arguments[index + 1]));
        }

        fields = pairs;
        return true;
    }

    internal sealed class BuiltInShellTypeDefinition : IShellNamedType
    {
        private readonly Func<IReadOnlyList<object?>, object> _factory;
        private readonly IReadOnlyList<ShellMemberDescriptor> _members;
        private readonly IReadOnlyList<ShellMethodDescriptor> _methods;
        private readonly IReadOnlyList<ShellConstructorDescriptor> _constructors;

        public BuiltInShellTypeDefinition(
            string name,
            Type runtimeType,
            Func<IReadOnlyList<object?>, object> factory,
            IReadOnlyList<ShellConstructorDescriptor> constructors,
            bool isArray = false)
        {
            Name = name;
            RuntimeType = runtimeType;
            _factory = factory;
            _constructors = constructors;
            _members = BuildMembers(runtimeType, isArray);
            _methods = BuildMethods(runtimeType);
            ShellFullName = $"ToSh.{name}";
            ShellNamespace = "ToSh";
            ShellAssemblyName = "ToSh";
            ShellBaseTypeName = runtimeType.BaseType?.FullName ?? typeof(object).FullName;
            ShellIsClass = !isArray;
            ShellIsInterface = false;
            ShellIsEnum = false;
            ShellIsValueType = runtimeType.IsValueType;
            ShellIsAbstract = false;
            ShellIsGenericType = false;
            ShellIsArray = isArray;
            ShellIsPublic = true;
        }

        public string Name { get; }

        public Type RuntimeType { get; }

        public string ShellTypeName => Name;

        /// <summary>
        /// Renders as the shell type's name. Without this, displaying a
        /// descriptor — which is what <c>type-of</c> yields for shell
        /// types — printed this class's own CLR name, so
        /// <c>type-of [1, 2]</c> reported
        /// <c>Tosh.Runtime.BuiltInShellTypes+BuiltInShellTypeDefinition</c>
        /// instead of the type the user asked about.
        /// </summary>
        public override string ToString() => Name;

        public string ShellFullName { get; }

        public string? ShellNamespace { get; }

        public string? ShellAssemblyName { get; }

        public string? ShellBaseTypeName { get; }

        public bool ShellIsClass { get; }

        public bool ShellIsInterface { get; }

        public bool ShellIsEnum { get; }

        public bool ShellIsValueType { get; }

        public bool ShellIsAbstract { get; }

        public bool ShellIsGenericType { get; }

        public bool ShellIsArray { get; }

        public bool ShellIsPublic { get; }

        public object CreateInstance(IReadOnlyList<object?> arguments) => _factory(arguments);

        public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
        {
            throw new InvalidOperationException($"Static method '{methodName}' was not found on type '{Name}'.");
        }

        public bool TryGetStaticMember(string memberName, out object? value)
        {
            value = null;
            return false;
        }

        public bool TryGetMember(string name, out object? value, bool includeHidden = false)
        {
            value = name switch
            {
                "Name" => ShellTypeName,
                "FullName" => ShellFullName,
                "Namespace" => ShellNamespace,
                "Assembly" => ShellAssemblyName,
                "BaseType" => ShellBaseTypeName,
                "IsClass" => ShellIsClass,
                "IsInterface" => ShellIsInterface,
                "IsEnum" => ShellIsEnum,
                "IsValueType" => ShellIsValueType,
                "IsAbstract" => ShellIsAbstract,
                "IsGenericType" => ShellIsGenericType,
                "IsArray" => ShellIsArray,
                "IsPublic" => ShellIsPublic,
                "PropertyCount" => _members.Count(member => !member.IsStatic),
                "MethodCount" => _methods.Count(method => !method.IsStatic),
                "StaticMethodCount" => _methods.Count(method => method.IsStatic),
                "ConstructorCount" => _constructors.Count,
                _ => null,
            };

            return value is not null;
        }

        public bool TrySetMember(string name, object? value) => false;

        public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        {
            return
            [
                new KeyValuePair<string, object?>("Name", ShellTypeName),
                new KeyValuePair<string, object?>("FullName", ShellFullName),
                new KeyValuePair<string, object?>("Namespace", ShellNamespace),
                new KeyValuePair<string, object?>("Assembly", ShellAssemblyName),
                new KeyValuePair<string, object?>("BaseType", ShellBaseTypeName),
                new KeyValuePair<string, object?>("IsClass", ShellIsClass),
                new KeyValuePair<string, object?>("IsInterface", ShellIsInterface),
                new KeyValuePair<string, object?>("IsEnum", ShellIsEnum),
                new KeyValuePair<string, object?>("IsValueType", ShellIsValueType),
                new KeyValuePair<string, object?>("IsAbstract", ShellIsAbstract),
                new KeyValuePair<string, object?>("IsGenericType", ShellIsGenericType),
                new KeyValuePair<string, object?>("IsArray", ShellIsArray),
                new KeyValuePair<string, object?>("IsPublic", ShellIsPublic),
                new KeyValuePair<string, object?>("PropertyCount", _members.Count(member => !member.IsStatic)),
                new KeyValuePair<string, object?>("MethodCount", _methods.Count(method => !method.IsStatic)),
                new KeyValuePair<string, object?>("StaticMethodCount", _methods.Count(method => method.IsStatic)),
                new KeyValuePair<string, object?>("ConstructorCount", _constructors.Count),
            ];
        }

        public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) => _members;

        public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) => _methods;

        public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() => _constructors;

        private static IReadOnlyList<ShellMemberDescriptor> BuildMembers(Type runtimeType, bool isArray)
        {
            return runtimeType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => new ShellMemberDescriptor(
                    property.Name,
                    Kind: "Property",
                    TypeName: ReflectionMetadataUtilities.GetDisplayName(property.PropertyType),
                    IsStatic: false,
                    IsWritable: property.CanWrite))
                .Concat(runtimeType
                    .GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Select(field => new ShellMemberDescriptor(
                        field.Name,
                        Kind: "Field",
                        TypeName: ReflectionMetadataUtilities.GetDisplayName(field.FieldType),
                        IsStatic: false,
                        IsWritable: !(field.IsInitOnly || field.IsLiteral))))
                .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<ShellMethodDescriptor> BuildMethods(Type runtimeType)
        {
            return runtimeType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName)
                .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
                .Select(method => new ShellMethodDescriptor(
                    method.Name,
                    ReturnTypeName: ReflectionMetadataUtilities.GetDisplayName(method.ReturnType),
                    IsStatic: false,
                    ParameterCount: method.GetParameters().Length,
                    Signature: ReflectionMetadataUtilities.FormatMethodSignature(method)))
                .ToArray();
        }
    }
}

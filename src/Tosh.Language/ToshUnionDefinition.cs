using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshUnionDefinition : IShellNamedType
{
    private readonly ToshEngine _owner;
    private readonly Dictionary<string, UnionVariantDefinition> _variantsByName;

    public ToshUnionDefinition(
        ToshEngine owner,
        string name,
        IReadOnlyList<UnionVariantDefinition> variants,
        IReadOnlyList<string>? typeParameters,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        _owner = owner;
        Name = name;
        Variants = variants;
        TypeParameterNames = typeParameters ?? Array.Empty<string>();
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        _variantsByName = variants.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public IReadOnlyList<UnionVariantDefinition> Variants { get; }

    public IReadOnlyList<string> TypeParameterNames { get; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => null;

    public bool ShellIsClass => false;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => TypeParameterNames.Count > 0;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        throw new InvalidOperationException(
            $"Cannot create a union '{Name}' directly. Use a variant constructor like {Name}.{Variants[0].Name}(...).");
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
        => InvokeVariant(methodName, arguments, explicitTypeArguments: null);

    /// <summary>Constructs a variant with an explicit closed generic argument list.</summary>
    public InvocationResult InvokeGenericVariant(
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<string> explicitTypeArguments)
        => InvokeVariant(methodName, arguments, explicitTypeArguments);

    private InvocationResult InvokeVariant(
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<string>? explicitTypeArguments)
    {
        if (!_variantsByName.TryGetValue(methodName, out var variant))
        {
            throw new InvalidOperationException($"Union '{Name}' has no variant named '{methodName}'.");
        }

        if (arguments.Count != variant.Fields.Count)
        {
            throw new InvalidOperationException(
                $"Variant '{Name}.{variant.Name}' expects {variant.Fields.Count} argument(s), but got {arguments.Count}.");
        }

        var typeArguments = BindTypeArguments(variant, arguments, explicitTypeArguments);

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < variant.Fields.Count; i++)
        {
            var field = variant.Fields[i];
            var value = arguments[i];
            if (field.TypeName is { Length: > 0 } rawTypeName)
            {
                var typeName = SubstituteTypeParameters(rawTypeName, typeArguments);
                value = _owner.ConvertValueToAnnotatedType(
                    typeName,
                    value,
                    field.Span.Start,
                    field.Span.Length,
                    SourceName,
                    SourceText,
                    $"field '{Name}.{variant.Name}.{field.Name}'");
            }

            fields[field.Name] = value;
        }

        var instance = new ToshUnionVariantInstance(this, variant.Name, fields, typeArguments);
        return new InvocationResult(instance, ReturnedVoid: false);
    }

    private IReadOnlyDictionary<string, string>? BindTypeArguments(
        UnionVariantDefinition variant,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<string>? explicitTypeArguments)
    {
        if (TypeParameterNames.Count == 0)
        {
            if (explicitTypeArguments is { Count: > 0 })
            {
                throw new InvalidOperationException($"Union '{Name}' is not generic and does not accept type arguments.");
            }

            return null;
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (explicitTypeArguments is not null)
        {
            if (explicitTypeArguments.Count != TypeParameterNames.Count)
            {
                throw new InvalidOperationException(
                    $"Generic union '{Name}' expects {TypeParameterNames.Count} type argument(s) " +
                    $"<{string.Join(", ", TypeParameterNames)}> but received {explicitTypeArguments.Count}.");
            }

            for (var i = 0; i < TypeParameterNames.Count; i++)
            {
                _owner.ValidateUnionTypeArgument(explicitTypeArguments[i], Span, SourceName, SourceText, Name);
                bindings[TypeParameterNames[i]] = explicitTypeArguments[i];
            }

            return bindings;
        }

        // Match generic-class construction: infer a direct `T` payload from the value when
        // possible, but require an explicit list for type parameters that do not occur in the
        // selected variant (notably generic unit variants).
        for (var i = 0; i < variant.Fields.Count; i++)
        {
            var declared = variant.Fields[i].TypeName;
            if (declared is null || !TypeParameterNames.Contains(declared, StringComparer.Ordinal))
            {
                continue;
            }

            var inferred = InferTypeName(arguments[i]);
            if (bindings.TryGetValue(declared, out var existing) &&
                !TypeNamesEqual(existing, inferred))
            {
                throw new InvalidOperationException(
                    $"Cannot infer one type argument for '{declared}' from both '{existing}' and '{inferred}'.");
            }
            bindings[declared] = inferred;
        }

        // `TOAST-0096`. Nothing in the arguments names the parameters of a unit variant —
        // `Option::None()` above all — so where the value is going supplies them.
        //
        // `TOAST-0083`. It also *overrides* what the arguments inferred, because an annotation
        // is a declaration and a value is a guess. Inferring from the value can only report the
        // CLR type it happens to have, and every declared record is one `ToshRecordInstance`, so
        // a `list<Diag>` payload inferred as `list<Tosh.Language.ToshRecordInstance>` and the
        // instance could not match the very annotation it was built for. A genuine disagreement
        // is still caught: a variant field with a declared type checks its argument.
        _owner.TryBindUnionTypeArgumentsFromTarget(Name, TypeParameterNames, bindings);

        var missing = TypeParameterNames.Where(name => !bindings.ContainsKey(name)).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Generic union '{Name}' requires explicit type arguments because " +
                $"{string.Join(", ", missing.Select(name => $"'{name}'"))} cannot be inferred. " +
                $"Call {Name}.{variant.Name}<{string.Join(", ", TypeParameterNames)}>(...).");
        }

        return bindings;
    }

    private static string SubstituteTypeParameters(
        string typeName,
        IReadOnlyDictionary<string, string>? bindings)
    {
        if (bindings is null || bindings.Count == 0) return typeName;

        var result = typeName;
        foreach (var (parameter, argument) in bindings)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(parameter)}\b",
                argument,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        return result;
    }

    private static string InferTypeName(object? value)
    {
        if (value is IShellTypedObject typed)
        {
            return typed.ShellTypeDescriptor.ShellTypeName;
        }

        return value is null ? "any?" : DescribeClrType(value.GetType());
    }

    private static readonly IReadOnlyDictionary<Type, string> PrimitiveShellNames =
        new Dictionary<Type, string>
        {
            [typeof(bool)] = "bool",
            [typeof(byte)] = "byte",
            [typeof(sbyte)] = "sbyte",
            [typeof(short)] = "short",
            [typeof(ushort)] = "ushort",
            [typeof(int)] = "int",
            [typeof(uint)] = "uint",
            [typeof(long)] = "long",
            [typeof(ulong)] = "ulong",
            [typeof(float)] = "float",
            [typeof(double)] = "double",
            [typeof(decimal)] = "decimal",
            [typeof(string)] = "string",
            [typeof(char)] = "char",
            [typeof(object)] = "dynamic",
        };

    private static readonly IReadOnlyDictionary<Type, string> GenericShellNames =
        new Dictionary<Type, string>
        {
            [typeof(List<>)] = "list",
            [typeof(Dictionary<,>)] = "dict",
            [typeof(HashSet<>)] = "set",
            [typeof(Queue<>)] = "queue",
            [typeof(Stack<>)] = "stack",
            [typeof(LinkedList<>)] = "linkedlist",
            [typeof(SortedSet<>)] = "sortedset",
            [typeof(SortedDictionary<,>)] = "sorteddict",
        };

    /// <summary>
    /// A CLR type in the spelling an annotation would use — <c>TOAST-0083</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inferring a type argument from a value used to answer with the shell alias for a
    /// primitive and the raw CLR name for everything else, so a `list&lt;int&gt;` payload
    /// inferred as
    /// <c>System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib, …]]</c>.
    /// The instance then reported that as its type argument, and
    /// <see cref="ToshUnionVariantInstance.IsInstanceOf"/> compares the annotation against that
    /// text — so `Result&lt;string, list&lt;int&gt;&gt;` could never match a value it had just
    /// built, and the function returning it failed its own return-type conversion.
    /// </para>
    /// <para>
    /// It only ever worked for arguments whose two spellings coincide — `int`, `string` — which
    /// is why a generic payload was the shape that broke, and why accumulating diagnostics in a
    /// `Result&lt;T, list&lt;Diag&gt;&gt;` was the fixture that found it.
    /// </para>
    /// </remarks>
    private static string DescribeClrType(Type type)
    {
        if (PrimitiveShellNames.TryGetValue(type, out var primitive))
        {
            return primitive;
        }

        if (type.IsArray && type.GetElementType() is { } element)
        {
            return $"array<{DescribeClrType(element)}>";
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = GenericShellNames.TryGetValue(definition, out var alias)
                ? alias
                : StripGenericArity(definition.Name);
            var arguments = type.GetGenericArguments().Select(DescribeClrType);
            return $"{name}<{string.Join(", ", arguments)}>";
        }

        return type.FullName ?? type.Name;
    }

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    private static bool TypeNamesEqual(string left, string right) =>
        string.Equals(RemoveWhitespace(left), RemoveWhitespace(right), StringComparison.OrdinalIgnoreCase);

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        if (_variantsByName.TryGetValue(memberName, out var variant))
        {
            if (variant.Fields.Count == 0)
            {
                if (TypeParameterNames.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Generic unit variant '{Name}.{memberName}' requires explicit type arguments. " +
                        $"Call {Name}.{memberName}<{string.Join(", ", TypeParameterNames)}>().");
                }
                // Unit variant — return the instance directly
                value = new ToshUnionVariantInstance(this, variant.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), typeArguments: null);
                return true;
            }

            // Has fields — hint that it needs to be called
            throw new InvalidOperationException(
                $"'{memberName}' is a variant of union '{Name}'. Call it with arguments: {Name}.{memberName}(...)");
        }

        value = null;
        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        foreach (var field in GetMembers(includeHidden))
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", ShellTypeName),
            new KeyValuePair<string, object?>("FullName", ShellFullName),
            new KeyValuePair<string, object?>("IsUnion", true),
            new KeyValuePair<string, object?>("VariantCount", Variants.Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) =>
        Array.Empty<ShellMemberDescriptor>();

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        return Variants
            .Where(v => v.FieldNames.Count > 0)
            .Select(v => new ShellMethodDescriptor(
                v.Name,
                ReturnTypeName: Name,
                IsStatic: true,
                ParameterCount: v.FieldNames.Count,
                Signature: $"{v.Name}({string.Join(", ", v.FieldNames)})",
                IsHidden: false))
            .ToArray();
    }

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() =>
        Array.Empty<ShellConstructorDescriptor>();
}

public sealed record UnionVariantDefinition(
    string Name,
    IReadOnlyList<UnionVariantFieldDefinition> Fields)
{
    public IReadOnlyList<string> FieldNames => Fields.Select(payload => payload.Name).ToArray();
}

public sealed record UnionVariantFieldDefinition(
    string Name,
    string? TypeName,
    TextSpan Span);

public sealed class ToshUnionVariantInstance : IShellRecordObject, IShellTypedObject, IShellTypeCheckable
{
    private readonly Dictionary<string, object?> _fields;

    public ToshUnionVariantInstance(
        ToshUnionDefinition unionDefinition,
        string variantName,
        Dictionary<string, object?> fields,
        IReadOnlyDictionary<string, string>? typeArguments)
    {
        UnionDefinition = unionDefinition;
        VariantName = variantName;
        _fields = fields;
        TypeArguments = typeArguments;
    }

    public ToshUnionDefinition UnionDefinition { get; }

    public string VariantName { get; }

    public IReadOnlyDictionary<string, string>? TypeArguments { get; }

    public string ShellTypeName => $"{UnionDefinition.Name}.{VariantName}";

    public IShellTypeDescriptor ShellTypeDescriptor => TypeArguments is { Count: > 0 }
        ? new BoundGenericUnionTypeDescriptor(UnionDefinition, TypeArguments)
        : UnionDefinition;

    public bool IsInstanceOf(string typeName)
    {
        if (string.Equals(typeName, UnionDefinition.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TypeArguments is not { Count: > 0 })
        {
            return false;
        }

        return string.Equals(
            string.Concat(typeName.Where(character => !char.IsWhiteSpace(character))),
            string.Concat(ShellTypeDescriptor.ShellTypeName.Where(character => !char.IsWhiteSpace(character))),
            StringComparison.OrdinalIgnoreCase);
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (string.Equals(name, "Variant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Tag", StringComparison.OrdinalIgnoreCase))
        {
            value = VariantName;
            return true;
        }

        return _fields.TryGetValue(name, out value);
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>
        {
            new("Variant", VariantName)
        };

        foreach (var (key, val) in _fields)
        {
            members.Add(new KeyValuePair<string, object?>(key, val));
        }

        return members;
    }

    public override string ToString()
    {
        if (_fields.Count == 0) return $"{UnionDefinition.Name}.{VariantName}";
        var fieldValues = string.Join(", ", _fields.Values.Select(v => v?.ToString() ?? "null"));
        return $"{UnionDefinition.Name}.{VariantName}({fieldValues})";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not ToshUnionVariantInstance other) return false;
        if (!string.Equals(UnionDefinition.Name, other.UnionDefinition.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(VariantName, other.VariantName, StringComparison.OrdinalIgnoreCase)) return false;
        if (_fields.Count != other._fields.Count) return false;

        foreach (var (key, val) in _fields)
        {
            if (!other._fields.TryGetValue(key, out var otherVal)) return false;
            if (!Equals(val, otherVal)) return false;
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(UnionDefinition.Name, VariantName);
        foreach (var (key, val) in _fields)
        {
            hash = HashCode.Combine(hash, key, val);
        }
        return hash;
    }
}

internal sealed class BoundGenericUnionTypeDescriptor : IShellTypeDescriptor
{
    private readonly ToshUnionDefinition _definition;
    private readonly string _displayName;

    public BoundGenericUnionTypeDescriptor(
        ToshUnionDefinition definition,
        IReadOnlyDictionary<string, string> bindings)
    {
        _definition = definition;
        var arguments = definition.TypeParameterNames
            .Select(name => bindings.TryGetValue(name, out var value) ? value : name);
        _displayName = $"{definition.Name}<{string.Join(", ", arguments)}>";
    }

    public string ShellTypeName => _displayName;
    public string ShellFullName => _displayName;
    public string? ShellNamespace => _definition.ShellNamespace;
    public string? ShellAssemblyName => _definition.ShellAssemblyName;
    public string? ShellBaseTypeName => _definition.ShellBaseTypeName;
    public bool ShellIsClass => _definition.ShellIsClass;
    public bool ShellIsInterface => _definition.ShellIsInterface;
    public bool ShellIsEnum => _definition.ShellIsEnum;
    public bool ShellIsValueType => _definition.ShellIsValueType;
    public bool ShellIsAbstract => _definition.ShellIsAbstract;
    public bool ShellIsGenericType => true;
    public bool ShellIsArray => _definition.ShellIsArray;
    public bool ShellIsPublic => _definition.ShellIsPublic;
    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) =>
        _definition.GetShellMembers(includeHidden);
    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) =>
        _definition.GetShellMethods(includeHidden);
    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() =>
        _definition.GetShellConstructors();
    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "FullName", StringComparison.OrdinalIgnoreCase))
        {
            value = _displayName;
            return true;
        }

        return _definition.TryGetMember(name, out value, includeHidden);
    }
    public bool TrySetMember(string name, object? value) => false;
    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = _definition.GetMembers(includeHidden)
            .Where(member => !string.Equals(member.Key, "Name", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(member.Key, "FullName", StringComparison.OrdinalIgnoreCase))
            .ToList();
        members.Insert(0, new KeyValuePair<string, object?>("FullName", _displayName));
        members.Insert(0, new KeyValuePair<string, object?>("Name", _displayName));
        return members;
    }
    public override string ToString() => _displayName;
}

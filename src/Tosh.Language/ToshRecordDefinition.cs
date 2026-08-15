using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshRecordDefinition : IShellNamedType
{
    /// <summary>The declaration's own `##` documentation (`TS-P2-101`).</summary>
    public DocComment? Documentation { get; internal set; }

    /// <inheritdoc />
    public string? ShellDocumentation => Documentation?.Description is { Length: > 0 } summary
        ? summary
        : null;

    private readonly ToshEngine _engine;
    private readonly Dictionary<string, ToshRecordFieldDefinition> _fieldsByName;

    public ToshRecordDefinition(
        ToshEngine engine,
        string name,
        IReadOnlyList<ToshRecordFieldDefinition> fields,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<LexicalScope>? capturedScopes,
        IReadOnlyList<string>? typeParameterNames = null,
        IReadOnlyList<TypeParameterConstraintSyntax>? typeParameterConstraints = null,
        DocComment? documentation = null)
    {
        Documentation = documentation;
        _engine = engine;
        Name = name;
        Fields = fields;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        CapturedScopes = capturedScopes;
        TypeParameterNames = typeParameterNames ?? Array.Empty<string>();
        TypeParameterConstraints = typeParameterConstraints ?? Array.Empty<TypeParameterConstraintSyntax>();
        _fieldsByName = fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public IReadOnlyList<ToshRecordFieldDefinition> Fields { get; private set; }

    public bool IsSealed { get; internal set; }

    public bool IsStrict { get; internal set; }

    public bool IsPartial { get; internal set; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public IReadOnlyList<LexicalScope>? CapturedScopes { get; }

    public IReadOnlyList<string> TypeParameterNames { get; }

    public IReadOnlyList<TypeParameterConstraintSyntax> TypeParameterConstraints { get; }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(object).FullName;

    public bool ShellIsClass => true;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => TypeParameterNames.Count > 0;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        return CreateInstanceCore(arguments, typeArgumentBindings: null);
    }

    /// <summary>
    /// Generic-aware construction. Mirrors
    /// <see cref="ToshClassDefinition.CreateGenericInstance"/> but for
    /// records (no methods, just typed fields).
    /// </summary>
    public object CreateGenericInstance(
        IReadOnlyList<Type?> resolvedTypeArguments,
        IReadOnlyList<string> typeArgumentDisplay,
        IReadOnlyList<object?> arguments)
    {
        if (resolvedTypeArguments.Count != TypeParameterNames.Count)
        {
            throw new InvalidOperationException(
                $"Generic record '{Name}' expects {TypeParameterNames.Count} type argument(s) " +
                $"<{string.Join(", ", TypeParameterNames)}> but received {resolvedTypeArguments.Count}.");
        }

        var bindings = new Dictionary<string, Type?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < TypeParameterNames.Count; i++)
        {
            bindings[TypeParameterNames[i]] = resolvedTypeArguments[i];
        }

        ValidateTypeParameterConstraints(bindings, typeArgumentDisplay);

        return CreateInstanceCore(arguments, bindings);
    }

    private void ValidateTypeParameterConstraints(
        IReadOnlyDictionary<string, Type?> bindings,
        IReadOnlyList<string> typeArgumentDisplay)
    {
        if (TypeParameterConstraints.Count == 0) return;
        foreach (var clause in TypeParameterConstraints)
        {
            if (!bindings.TryGetValue(clause.TypeParameter, out var bound) || bound is null)
            {
                continue;
            }
            foreach (var constraintName in clause.ConstraintNames)
            {
                bool satisfied;
                bool known;
                if (ToshTypeParameterConstraintRegistry.TryGet(constraintName, out var predicate))
                {
                    satisfied = predicate(bound);
                    known = true;
                }
                else
                {
                    var clr = _engine.TryResolveTypeName(constraintName);
                    if (clr is not null)
                    {
                        satisfied = clr.IsAssignableFrom(bound);
                        known = true;
                    }
                    else
                    {
                        satisfied = true;
                        known = false;
                    }
                }

                if (satisfied) continue;
                if (!known) continue;

                var displayIndex = TypeParameterNames
                    .Select((n, i) => (n, i))
                    .FirstOrDefault(t => string.Equals(t.n, clause.TypeParameter, StringComparison.OrdinalIgnoreCase)).i;
                var argDisplay = displayIndex < typeArgumentDisplay.Count
                    ? typeArgumentDisplay[displayIndex]
                    : bound.Name;
                throw new InvalidOperationException(
                    $"Generic record '{Name}' requires type parameter '{clause.TypeParameter}' to satisfy '{constraintName}', " +
                    $"but '{argDisplay}' (CLR {bound.FullName ?? bound.Name}) does not.");
            }
        }
    }

    private object CreateInstanceCore(
        IReadOnlyList<object?> arguments,
        IReadOnlyDictionary<string, Type?>? typeArgumentBindings)
    {
        if (TypeParameterNames.Count > 0 && typeArgumentBindings is null)
        {
            throw new InvalidOperationException(
                $"Generic record '{Name}' requires type arguments, e.g. 'new {Name}<{string.Join(", ", TypeParameterNames)}>(…)'.");
        }

        var instance = new ToshRecordInstance(this, typeArgumentBindings);
        var bound = BindFields(arguments, typeArgumentBindings);

        foreach (var field in Fields)
        {
            bound.TryGetValue(field.Name, out var value);
            instance.SetStoredValue(field.Name, value);
        }

        return instance;
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        throw new InvalidOperationException($"Static method '{methodName}' was not found on record '{Name}'.");
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
            "FieldCount" => Fields.Count,
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
            new KeyValuePair<string, object?>("FieldCount", Fields.Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
    {
        return Fields
            .Select(field => new ShellMemberDescriptor(
                field.Name,
                Kind: "Property",
                TypeName: field.TypeName ?? "object",
                IsStatic: false,
                IsWritable: true,
                IsHidden: false))
            .ToArray();
    }

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) => [];

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors()
    {
        return
        [
            new ShellConstructorDescriptor(
                Fields.Count,
                $"{Name}({string.Join(", ", Fields.Select(field => $"{(field.TypeName ?? "object")} {field.Name}"))})"),
        ];
    }

    internal bool TryGetField(string name, out ToshRecordFieldDefinition field) => _fieldsByName.TryGetValue(name, out field!);

    internal object? ConvertFieldValue(ToshRecordFieldDefinition field, object? value)
    {
        return ConvertFieldValue(field, value, typeArgumentBindings: null);
    }

    internal object? ConvertFieldValue(
        ToshRecordFieldDefinition field,
        object? value,
        IReadOnlyDictionary<string, Type?>? typeArgumentBindings)
    {
        // If the field's annotation is a type-parameter name, perform a
        // strict IsInstanceOfType check against the bound CLR type — no
        // coercion. Mirrors generic-class parameter behavior.
        if (field.TypeName is not null
            && typeArgumentBindings is not null
            && typeArgumentBindings.TryGetValue(field.TypeName, out var boundType)
            && boundType is not null)
        {
            if (value is null) return null;
            if (!boundType.IsInstanceOfType(value))
            {
                throw new InvalidOperationException(
                    $"Record field '{Name}.{field.Name}' expects type parameter '{field.TypeName}' bound to '{boundType.FullName ?? boundType.Name}', " +
                    $"but received a value of type '{value.GetType().FullName ?? value.GetType().Name}'.");
            }
            return value;
        }

        return _engine.ConvertAnnotatedValue(
            field.TypeName,
            field.Refinement,
            value,
            field.Span,
            SourceName,
            SourceText,
            $"{Name}.{field.Name}");
    }

    private Dictionary<string, object?> BindFields(
        IReadOnlyList<object?> arguments,
        IReadOnlyDictionary<string, Type?>? typeArgumentBindings = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        // `TS-P2-21`. A named argument is placed on its field rather than consuming a position,
        // so `new R("w", Qty = 5)` reaches `Qty` instead of assigning the wrapper to it.
        var (positional, named) = FieldArgumentPlacement.Split(arguments, "Record", Name);
        FieldArgumentPlacement.EnsureNamesAreKnown(named, Fields.Select(f => f.Name), "Record", Name);

        var positionalIndex = 0;

        for (var index = 0; index < Fields.Count; index++)
        {
            var field = Fields[index];

            if (named.TryGetValue(field.Name, out var namedValue))
            {
                values[field.Name] = ConvertFieldValue(field, namedValue, typeArgumentBindings);
                continue;
            }

            if (positionalIndex < positional.Count)
            {
                values[field.Name] = ConvertFieldValue(field, positional[positionalIndex++], typeArgumentBindings);
                continue;
            }

            if (field.DefaultValue is not null)
            {
                var defaultValue = _engine.EvaluateClassPipelineValueSync(null, SourceName, SourceText, field.DefaultValue, values, CapturedScopes);
                values[field.Name] = ConvertFieldValue(field, defaultValue, typeArgumentBindings);
                continue;
            }

            if (field.IsOptional)
            {
                values[field.Name] = null;
                continue;
            }

            throw new InvalidOperationException($"No value was provided for required record field '{field.Name}' on '{Name}'.");
        }

        if (positional.Count + named.Count > Fields.Count)
        {
            throw new InvalidOperationException($"Record '{Name}' expects {Fields.Count} argument(s) but received {positional.Count + named.Count}.");
        }

        return values;
    }

    internal void MergePartial(IReadOnlyList<ToshRecordFieldDefinition> newFields)
    {
        var merged = new List<ToshRecordFieldDefinition>(Fields);
        foreach (var field in newFields)
        {
            if (!_fieldsByName.ContainsKey(field.Name))
            {
                merged.Add(field);
                _fieldsByName[field.Name] = field;
            }
        }
        Fields = merged;
    }
}

public sealed record ToshRecordFieldDefinition(
    string Name,
    string? TypeName,
    PipelineSyntax? DefaultValue,
    bool IsOptional,
    TextSpan Span,
    RefinementAnnotation? Refinement = null);

using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshStructDefinition : IShellNamedType
{
    private readonly ToshEngine _engine;
    private readonly Dictionary<string, ToshRecordFieldDefinition> _fieldsByName;

    public ToshStructDefinition(
        ToshEngine engine,
        string name,
        IReadOnlyList<ToshRecordFieldDefinition> fields,
        IReadOnlyList<ToshClassPropertyDefinition> properties,
        IReadOnlyList<ToshClassMethodDefinition> methods,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<LexicalScope>? capturedScopes)
    {
        _engine = engine;
        Name = name;
        Fields = fields;
        Properties = properties;
        Methods = methods;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        CapturedScopes = capturedScopes;
        _fieldsByName = fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public IReadOnlyList<ToshRecordFieldDefinition> Fields { get; private set; }

    public IReadOnlyList<ToshClassPropertyDefinition> Properties { get; }

    public IReadOnlyList<ToshClassMethodDefinition> Methods { get; }

    public bool IsSealed { get; internal set; }

    public bool IsFluid { get; internal set; }

    public bool IsPartial { get; internal set; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public IReadOnlyList<LexicalScope>? CapturedScopes { get; }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(ValueType).FullName;

    public bool ShellIsClass => false;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => true;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        var instance = new ToshStructInstance(this);
        var bound = BindFields(arguments);

        foreach (var field in Fields)
        {
            bound.TryGetValue(field.Name, out var value);
            instance.SetStoredValue(field.Name, value);
        }

        return instance;
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        var method = Methods.FirstOrDefault(m =>
            m.IsStatic &&
            string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));

        if (method is null)
        {
            throw new InvalidOperationException($"Static method '{methodName}' was not found on struct '{Name}'.");
        }

        var values = _engine.InvokeStructStaticMethodAsync(this, method, arguments);
        var result = values.ToBlockingEnumerable().LastOrDefault();
        return new InvocationResult(result, ReturnedVoid: false);
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        var method = Methods.FirstOrDefault(m =>
            m.IsStatic &&
            string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));

        if (method is not null)
        {
            value = new ToshStructStaticMethodReference(this, method, _engine);
            return true;
        }

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
            "IsValueType" => true,
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
            new KeyValuePair<string, object?>("IsValueType", true),
            new KeyValuePair<string, object?>("FieldCount", Fields.Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
    {
        return Fields
            .Select(field => new ShellMemberDescriptor(
                field.Name,
                Kind: "Field",
                TypeName: field.TypeName ?? "object",
                IsStatic: false,
                IsWritable: IsFluid,
                IsHidden: false))
            .ToArray();
    }

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        return Methods
            .Select(m => new ShellMethodDescriptor(
                m.Name,
                ReturnTypeName: m.ReturnTypeName ?? "any",
                IsStatic: m.IsStatic,
                ParameterCount: m.Parameters.Count,
                Signature: $"{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Name))})",
                IsHidden: m.IsShy && !includeHidden))
            .Where(m => !m.IsHidden)
            .ToArray();
    }

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

    internal IAsyncEnumerable<object?> InvokeStructInstanceMethodAsync(
        ToshStructInstance instance,
        ToshClassMethodDefinition method,
        IReadOnlyList<object?> arguments)
    {
        // Create locals with 'this' reference and bound parameters
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < method.Parameters.Count && i < arguments.Count; i++)
        {
            locals[method.Parameters[i].Name] = arguments[i];
        }
        locals["this"] = instance;
        locals["args"] = arguments.ToArray();

        var values = _engine.ExecuteClassBlockSync(
            method.SourceName,
            method.SourceText,
            method.Body,
            locals,
            method.CapturedScopes,
            $"{Name}.{method.Name}");

        return values.ToAsyncEnumerable();
    }

    internal object? ConvertFieldValue(ToshRecordFieldDefinition field, object? value)
    {
        return _engine.ConvertAnnotatedValue(
            field.TypeName,
            field.Refinement,
            value,
            field.Span,
            SourceName,
            SourceText,
            $"{Name}.{field.Name}");
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

    private Dictionary<string, object?> BindFields(IReadOnlyList<object?> arguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var index = 0; index < Fields.Count; index++)
        {
            var field = Fields[index];

            if (index < arguments.Count)
            {
                values[field.Name] = ConvertFieldValue(field, arguments[index]);
                continue;
            }

            if (field.DefaultValue is not null)
            {
                var defaultValue = _engine.EvaluateClassPipelineValueSync(SourceName, SourceText, field.DefaultValue, values, CapturedScopes);
                values[field.Name] = ConvertFieldValue(field, defaultValue);
                continue;
            }

            if (field.IsOptional)
            {
                values[field.Name] = null;
                continue;
            }

            throw new InvalidOperationException($"No value was provided for required struct field '{field.Name}' on '{Name}'.");
        }

        if (arguments.Count > Fields.Count)
        {
            throw new InvalidOperationException($"Struct '{Name}' expects {Fields.Count} argument(s) but received {arguments.Count}.");
        }

        return values;
    }
}

internal sealed class ToshStructStaticMethodReference : IShellInvocableObject
{
    private readonly ToshStructDefinition _definition;
    private readonly ToshClassMethodDefinition _method;
    private readonly ToshEngine _engine;

    public ToshStructStaticMethodReference(ToshStructDefinition definition, ToshClassMethodDefinition method, ToshEngine engine)
    {
        _definition = definition;
        _method = method;
        _engine = engine;
    }

    public InvocationResult InvokeAsync(IReadOnlyList<object?> arguments, CancellationToken cancellationToken = default)
    {
        var values = _engine.InvokeStructStaticMethodAsync(_definition, _method, arguments);
        var result = values.ToBlockingEnumerable().LastOrDefault();
        return new InvocationResult(result, ReturnedVoid: false);
    }

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments) =>
        throw new InvalidOperationException($"Static method reference does not support instance method invocation.");
}

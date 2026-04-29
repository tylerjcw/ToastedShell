using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshRecordDefinition : IShellNamedType
{
    private readonly ToshEngine _engine;
    private readonly Dictionary<string, ToshRecordFieldDefinition> _fieldsByName;

    public ToshRecordDefinition(
        ToshEngine engine,
        string name,
        IReadOnlyList<ToshRecordFieldDefinition> fields,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<LexicalScope>? capturedScopes)
    {
        _engine = engine;
        Name = name;
        Fields = fields;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        CapturedScopes = capturedScopes;
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

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        var instance = new ToshRecordInstance(this);
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
        return _engine.ConvertAnnotatedValue(
            field.TypeName,
            field.Refinement,
            value,
            field.Span,
            SourceName,
            SourceText,
            $"{Name}.{field.Name}");
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

            throw new InvalidOperationException($"No value was provided for required record field '{field.Name}' on '{Name}'.");
        }

        if (arguments.Count > Fields.Count)
        {
            throw new InvalidOperationException($"Record '{Name}' expects {Fields.Count} argument(s) but received {arguments.Count}.");
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

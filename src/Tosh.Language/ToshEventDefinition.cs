using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshEventDefinition : IShellEventFactory
{
    private readonly ToshEngine _engine;

    public ToshEventDefinition(
        ToshEngine engine,
        string name,
        IReadOnlyList<ToshEventFieldDefinition> fields,
        bool isRequired,
        bool isLocal,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<LexicalScope>? capturedScopes)
    {
        _engine = engine;
        Name = name;
        Fields = fields;
        IsRequired = isRequired;
        IsLocal = isLocal;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        CapturedScopes = capturedScopes;
    }

    public string Name { get; }

    public string EventName => Name;

    public IReadOnlyList<ToshEventFieldDefinition> Fields { get; }

    public bool IsRequired { get; }

    public bool IsLocal { get; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public IReadOnlyList<LexicalScope>? CapturedScopes { get; }

    public ShellEvent CreateEvent(ShellEventSender sender) => CreateInstance(sender);

    public ShellEvent CreateEvent(ShellEventSender sender, IReadOnlyList<KeyValuePair<string, object?>> fieldOverrides) =>
        CreateInstance(sender, fieldOverrides);

    public ToshEventInstance CreateInstance(ShellEventSender sender)
    {
        var instance = new ToshEventInstance(this, sender);

        foreach (var field in Fields)
        {
            object? value = null;

            if (field.DefaultValue is not null)
            {
                value = _engine.EvaluateClassPipelineValueSync(
            null,
                    SourceName,
                    SourceText,
                    field.DefaultValue,
                    new Dictionary<string, object?>(StringComparer.Ordinal),
                    CapturedScopes);
            }

            instance.SetField(field.Name, value);
        }

        return instance;
    }

    public ToshEventInstance CreateInstance(ShellEventSender sender, IReadOnlyList<KeyValuePair<string, object?>> fieldValues)
    {
        var instance = new ToshEventInstance(this, sender);

        foreach (var field in Fields)
        {
            var pair = fieldValues.FirstOrDefault(kv => string.Equals(kv.Key, field.Name, StringComparison.OrdinalIgnoreCase));

            if (pair.Key is not null)
            {
                instance.SetField(field.Name, pair.Value);
            }
            else if (field.DefaultValue is not null)
            {
                var value = _engine.EvaluateClassPipelineValueSync(
            null,
                    SourceName,
                    SourceText,
                    field.DefaultValue,
                    new Dictionary<string, object?>(StringComparer.Ordinal),
                    CapturedScopes);
                instance.SetField(field.Name, value);
            }
            else
            {
                instance.SetField(field.Name, null);
            }
        }

        return instance;
    }

    public override string ToString() => $"[EventType: {Name}]";
}

public sealed record ToshEventFieldDefinition(
    string Name,
    string? TypeName,
    PipelineSyntax? DefaultValue,
    TextSpan Span);

public sealed class ToshEventInstance : ShellEvent
{
    private readonly Dictionary<string, object?> _fields = new(StringComparer.OrdinalIgnoreCase);

    public ToshEventInstance(ToshEventDefinition definition, ShellEventSender sender)
        : base(definition.Name, sender)
    {
        Definition = definition;
    }

    public ToshEventDefinition Definition { get; }

    public void SetField(string name, object? value)
    {
        _fields[name] = value;
    }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_fields.TryGetValue(name, out value))
        {
            return true;
        }

        return base.TryGetMember(name, out value, includeHidden);
    }

    public override bool TrySetMember(string name, object? value)
    {
        if (_fields.ContainsKey(name))
        {
            _fields[name] = value;
            return true;
        }

        return false;
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return [..baseMembers, .._fields];
    }

    public override string ToString() => $"[Event: {Name}]";
}

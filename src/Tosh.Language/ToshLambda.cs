using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ToshLambda : IShellCallable, IShellRecordObject
{
    private readonly FunctionDefinition _definition;
    private readonly ToshEngine _engine;

    public ToshLambda(ToshEngine engine, FunctionDefinition definition)
    {
        _engine = engine;
        _definition = definition;
    }

    public string CallableName => _definition.Name;

    internal FunctionDefinition Definition => _definition;

    public int RequiredParameterCount => _definition.Parameters.Count(parameter => !parameter.IsOptional && !parameter.IsRest);

    public int? MaximumParameterCount => _definition.Parameters.Count > 0 && _definition.Parameters[^1].IsRest
        ? null
        : _definition.Parameters.Count;

    public string ShellTypeName => "Lambda";

    public IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        // When executing inside a fork (race/settle/parallel), redirect to the fork's
        // engine so that scope mutations and call stacks stay isolated to the fork.
        if (context.BlockExecutor is ToshEngine.EngineBlockExecutor eb &&
            !ReferenceEquals(eb.Engine, _engine))
        {
            return eb.Engine.ExecuteFunctionAsync(_definition, context);
        }

        return _engine.ExecuteFunctionAsync(_definition, context);
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" => CallableName,
            "ParameterCount" => _definition.Parameters.Count,
            "RequiredParameterCount" => RequiredParameterCount,
            "MaximumParameterCount" => MaximumParameterCount,
            "Parameters" => _definition.Parameters.Select(parameter => parameter.Name).ToArray(),
            "ReturnType" => _definition.ReturnTypeName,
            "Source" => GetSourceSnippet(),
            _ => null,
        };

        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", CallableName),
            new KeyValuePair<string, object?>("ParameterCount", _definition.Parameters.Count),
            new KeyValuePair<string, object?>("RequiredParameterCount", RequiredParameterCount),
            new KeyValuePair<string, object?>("MaximumParameterCount", MaximumParameterCount),
            new KeyValuePair<string, object?>("Parameters", _definition.Parameters.Select(parameter => parameter.Name).ToArray()),
            new KeyValuePair<string, object?>("ReturnType", _definition.ReturnTypeName),
            new KeyValuePair<string, object?>("Source", GetSourceSnippet()),
        ];
    }

    public override string ToString()
    {
        var parameters = string.Join(", ", _definition.Parameters.Select(parameter => parameter.Name));
        return $"func({parameters})";
    }

    private string GetSourceSnippet()
    {
        if (_definition.Span.Start < 0 ||
            _definition.Span.End <= _definition.Span.Start ||
            _definition.Span.End > _definition.SourceText.Length)
        {
            return "func(...)";
        }

        return _definition.SourceText[_definition.Span.Start.._definition.Span.End].Trim();
    }
}

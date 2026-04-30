namespace Tosh.Core;

public sealed class PartialShellCallable : IShellCallable, IShellRecordObject
{
    private readonly IShellCallable _inner;
    private readonly object?[] _boundArguments;

    public PartialShellCallable(IShellCallable inner, IReadOnlyList<object?> boundArguments)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(boundArguments);
        _boundArguments = boundArguments.ToArray();
    }

    public string CallableName => $"partial({_inner.CallableName})";

    public int RequiredParameterCount => Math.Max(0, _inner.RequiredParameterCount - _boundArguments.Length);

    public int? MaximumParameterCount => _inner.MaximumParameterCount is int maximum
        ? Math.Max(0, maximum - _boundArguments.Length)
        : null;

    public string ShellTypeName => "PartialCallable";

    public IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var combinedArguments = new object?[_boundArguments.Length + context.Arguments.Count];
        Array.Copy(_boundArguments, combinedArguments, _boundArguments.Length);

        for (var index = 0; index < context.Arguments.Count; index++)
        {
            combinedArguments[_boundArguments.Length + index] = context.Arguments[index];
        }

        if (_inner.MaximumParameterCount is int maximum && combinedArguments.Length > maximum)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.partial_argument_count_mismatch",
                title: $"Callable '{_inner.CallableName}' accepts at most {maximum} argument(s) but received {combinedArguments.Length}.",
                label: "too many arguments were supplied to the partially applied callable");
        }

        var invokeContext = context with
        {
            Arguments = combinedArguments,
        };

        return _inner.InvokeAsync(invokeContext);
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" => CallableName,
            "InnerCallable" => _inner,
            "BoundArguments" => _boundArguments,
            "BoundCount" => _boundArguments.Length,
            "RequiredParameterCount" => RequiredParameterCount,
            "MaximumParameterCount" => MaximumParameterCount,
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
            new KeyValuePair<string, object?>("InnerCallable", _inner),
            new KeyValuePair<string, object?>("BoundArguments", _boundArguments),
            new KeyValuePair<string, object?>("BoundCount", _boundArguments.Length),
            new KeyValuePair<string, object?>("RequiredParameterCount", RequiredParameterCount),
            new KeyValuePair<string, object?>("MaximumParameterCount", MaximumParameterCount),
        ];
    }

    public override string ToString()
    {
        return $"partial {_inner.CallableName} [{_boundArguments.Length} bound]";
    }
}

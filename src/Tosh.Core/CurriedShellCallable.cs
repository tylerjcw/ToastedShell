namespace Tosh.Core;

public sealed class CurriedShellCallable : IShellCallable, IShellRecordObject
{
    private readonly IShellCallable _inner;
    private readonly object?[] _boundArguments;
    private readonly int _targetArity;

    public CurriedShellCallable(IShellCallable inner, IReadOnlyList<object?> boundArguments, int targetArity)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(boundArguments);
        _boundArguments = boundArguments.ToArray();
        _targetArity = targetArity;
    }

    public string CallableName => $"curry({_inner.CallableName})";

    public int RequiredParameterCount => _boundArguments.Length >= _targetArity ? 0 : 1;

    public int? MaximumParameterCount => _boundArguments.Length >= _targetArity
        ? 0
        : _targetArity - _boundArguments.Length;

    public string ShellTypeName => "CurriedCallable";

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var combinedArguments = new object?[_boundArguments.Length + context.Arguments.Count];
        Array.Copy(_boundArguments, combinedArguments, _boundArguments.Length);

        for (var index = 0; index < context.Arguments.Count; index++)
        {
            combinedArguments[_boundArguments.Length + index] = context.Arguments[index];
        }

        if (combinedArguments.Length > _targetArity)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.curry_argument_count_mismatch",
                title: $"Curried callable '{_inner.CallableName}' accepts {_targetArity} total argument(s) but received {combinedArguments.Length}.",
                label: "too many arguments were supplied to the curried callable");
        }

        if (combinedArguments.Length < _targetArity)
        {
            yield return new CurriedShellCallable(_inner, combinedArguments, _targetArity);
            yield break;
        }

        var invokeContext = context with
        {
            Arguments = combinedArguments,
        };

        await foreach (var value in _inner.InvokeAsync(invokeContext).WithCancellation(context.CancellationToken))
        {
            yield return value;
        }
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" => CallableName,
            "InnerCallable" => _inner,
            "BoundArguments" => _boundArguments,
            "BoundCount" => _boundArguments.Length,
            "TargetArity" => _targetArity,
            "RemainingParameterCount" => Math.Max(0, _targetArity - _boundArguments.Length),
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
            new KeyValuePair<string, object?>("TargetArity", _targetArity),
            new KeyValuePair<string, object?>("RemainingParameterCount", Math.Max(0, _targetArity - _boundArguments.Length)),
        ];
    }

    public override string ToString()
    {
        return $"curry {_inner.CallableName} [{_boundArguments.Length}/{_targetArity}]";
    }
}

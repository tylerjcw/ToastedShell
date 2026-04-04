namespace Tosh.Core;

public sealed class ComposedShellCallable : IShellCallable, IShellRecordObject
{
    private readonly IReadOnlyList<IShellCallable> _callables;

    public ComposedShellCallable(IReadOnlyList<IShellCallable> callables)
    {
        ArgumentNullException.ThrowIfNull(callables);

        if (callables.Count < 2)
        {
            throw new ArgumentException("Compose requires at least two callables.", nameof(callables));
        }

        _callables = callables;
    }

    public string CallableName => $"compose({string.Join(", ", _callables.Select(c => c.CallableName))})";

    public int RequiredParameterCount => _callables[0].RequiredParameterCount;

    public int? MaximumParameterCount => _callables[0].MaximumParameterCount;

    public string ShellTypeName => "ComposedCallable";

    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        // Execute first callable with the original arguments
        var results = await AsyncEnumerableExtensions.ToListAsync(
            _callables[0].InvokeAsync(context),
            context.CancellationToken);

        // Chain each subsequent callable, passing previous results as arguments
        for (var i = 1; i < _callables.Count; i++)
        {
            var chainContext = context with
            {
                Arguments = results,
                Input = AsyncEnumerableExtensions.Empty<object?>(),
                IsPipelined = false,
            };

            results = await AsyncEnumerableExtensions.ToListAsync(
                _callables[i].InvokeAsync(chainContext),
                context.CancellationToken);
        }

        foreach (var result in results)
        {
            yield return result;
        }
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" => CallableName,
            "Count" => _callables.Count,
            _ => null,
        };

        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new("Name", CallableName),
            new("Count", _callables.Count),
        ];
    }
}

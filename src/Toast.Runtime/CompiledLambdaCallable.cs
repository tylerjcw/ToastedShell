namespace Tosh.Runtime;

/// <summary>
/// A compiled lambda callable that wraps a compiled CLR method as an
/// <see cref="IShellCallable"/>. The compiled program emits one instance
/// per <c>func(params) =&gt; body</c> / <c>func(params) { … }</c> lambda
/// expression, replacing the interpreter's <c>ToshLambda</c> path
/// with a real CLR delegate.
///
/// The <paramref name="body"/> delegate receives:
/// <list type="bullet">
/// <item>arg 0 – the positional arguments array, one slot per parameter.</item>
/// <item>arg 1 – an array of captured outer-scope values snapshotted at
///   lambda-construction time.</item>
/// </list>
/// and returns a <see cref="List{T}"/> of the values the lambda produced.
/// </summary>
public sealed class CompiledLambdaCallable : IShellCallable
{
    private sealed class MissingArgumentSentinel
    {
        public override string ToString() => "<missing>";
    }

    public static readonly object MissingArgument = new MissingArgumentSentinel();

    private readonly Func<object?[], object?[], List<object?>> _body;
    private readonly object?[] _captureValues;
    private readonly string[] _parameterNames;
    private readonly bool[] _optionalParameters;
    private readonly Dictionary<string, int> _parameterIndexByName;
    private readonly int _requiredParamCount;
    private readonly int _maxParamCount;
    private readonly int _restParameterIndex;

    public CompiledLambdaCallable(
        Func<object?[], object?[], List<object?>> body,
        object?[] captureValues,
        string[] parameterNames,
        bool[] optionalParameters,
        int requiredParamCount,
        int maxParamCount,
        int restParameterIndex)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(captureValues);
        ArgumentNullException.ThrowIfNull(parameterNames);
        ArgumentNullException.ThrowIfNull(optionalParameters);

        if (parameterNames.Length != optionalParameters.Length)
        {
            throw new ArgumentException(
                "Compiled lambda parameter metadata arrays must have matching lengths.",
                nameof(optionalParameters));
        }

        _body = body;
        _captureValues = captureValues;
        _parameterNames = parameterNames;
        _optionalParameters = optionalParameters;
        _parameterIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < parameterNames.Length; index++)
        {
            _parameterIndexByName[parameterNames[index]] = index;
        }
        _requiredParamCount = requiredParamCount;
        _maxParamCount = maxParamCount;
        _restParameterIndex = restParameterIndex;
    }

    /// <inheritdoc/>
    public string CallableName => "<lambda>";

    /// <inheritdoc/>
    public int RequiredParameterCount => _requiredParamCount;

    /// <inheritdoc/>
    public int? MaximumParameterCount => _maxParamCount < 0 ? null : _maxParamCount;

    public int ParameterCount => _parameterNames.Length;

    public IAsyncEnumerable<object?> InvokeBoundAsync(
        CommandContext context,
        IReadOnlyList<object?> positionalArguments,
        IReadOnlyDictionary<string, object?> namedArguments)
    {
        var args = BindArguments(context, positionalArguments, namedArguments);
        return InvokeNormalizedAsync(args);
    }

#pragma warning disable CS1998 // async method lacks await – intentional: body is synchronous
    /// <inheritdoc/>
    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var args = BindArguments(
            context,
            context.Arguments,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

        foreach (var result in _body(args, _captureValues))
            yield return result;
    }

    private async IAsyncEnumerable<object?> InvokeNormalizedAsync(object?[] args)
    {
        foreach (var result in _body(args, _captureValues))
            yield return result;
    }
#pragma warning restore CS1998

    private object?[] BindArguments(
        CommandContext context,
        IReadOnlyList<object?> positionalArguments,
        IReadOnlyDictionary<string, object?> namedArguments)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(positionalArguments);
        ArgumentNullException.ThrowIfNull(namedArguments);

        foreach (var named in namedArguments)
        {
            if (!_parameterIndexByName.TryGetValue(named.Key, out var parameterIndex) ||
                parameterIndex == _restParameterIndex)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.callable_named_argument",
                    title: $"Callable '{CallableName}' has no parameter named '{named.Key}'.",
                    label: "this named argument does not match a callable parameter");
            }
        }

        var positionalParameterCount = _restParameterIndex >= 0
            ? _parameterNames.Length - 1
            : _parameterNames.Length;

        var requiredMissingNamedCount = 0;
        for (var index = 0; index < positionalParameterCount; index++)
        {
            if (!_optionalParameters[index] && !namedArguments.ContainsKey(_parameterNames[index]))
            {
                requiredMissingNamedCount++;
            }
        }

        var maximumPositionalCount = _restParameterIndex >= 0
            ? int.MaxValue
            : positionalParameterCount - namedArguments.Count;

        if (positionalArguments.Count < requiredMissingNamedCount ||
            positionalArguments.Count > maximumPositionalCount)
        {
            var received = positionalArguments.Count + namedArguments.Count;
            var expected = MaximumParameterCount is int maximum
                ? DescribeArity(_requiredParamCount, maximum)
                : $"at least {_requiredParamCount} argument(s)";

            throw context.CreateDiagnostic(
                code: "tosh.runtime.callable_argument_count_mismatch",
                title: $"Callable '{CallableName}' expects {expected} but received {received}.",
                label: $"'{CallableName}' requires {expected}");
        }

        var bound = new object?[_parameterNames.Length];
        var positionalIndex = 0;

        for (var parameterIndex = 0; parameterIndex < positionalParameterCount; parameterIndex++)
        {
            var parameterName = _parameterNames[parameterIndex];
            if (namedArguments.TryGetValue(parameterName, out var namedValue))
            {
                bound[parameterIndex] = namedValue;
                continue;
            }

            if (positionalIndex >= positionalArguments.Count)
            {
                bound[parameterIndex] = MissingArgument;
                continue;
            }

            bound[parameterIndex] = positionalArguments[positionalIndex++];
        }

        if (_restParameterIndex >= 0)
        {
            var restValues = new List<object?>();
            while (positionalIndex < positionalArguments.Count)
            {
                restValues.Add(positionalArguments[positionalIndex++]);
            }
            bound[_restParameterIndex] = restValues;
        }

        return bound;
    }

    private static string DescribeArity(int required, int maximum) =>
        required == maximum
            ? $"{required} argument(s)"
            : $"between {required} and {maximum} argument(s)";
}

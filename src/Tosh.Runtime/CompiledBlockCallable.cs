namespace Tosh.Runtime;

/// <summary>
/// A compiled-block callable that wraps a compiled CLR method as an
/// <see cref="IShellCallable"/>. The compiled program emits one instance
/// per <c>{ … }</c> block argument, replacing the source-replay
/// <see cref="ShellBlock"/> path with a real CLR delegate.
///
/// The <paramref name="body"/> delegate receives:
/// <list type="bullet">
/// <item>arg 0 – the pipeline item bound as <c>$_</c> by the calling command.</item>
/// <item>arg 1 – an array of captured outer-scope values snapshotted at block-construction time.</item>
/// </list>
/// and returns a <see cref="List{T}"/> of the values the block produces as pipeline output.
/// </summary>
public sealed class CompiledBlockCallable : IShellCallable
{
    private readonly Func<object?, object[], List<object?>> _body;
    private readonly object[] _captureValues;

    public CompiledBlockCallable(Func<object?, object[], List<object?>> body, object[] captureValues)
    {
        _body = body;
        _captureValues = captureValues;
    }

    /// <inheritdoc/>
    public string CallableName => "<compiled-block>";

    /// <inheritdoc/>
    public int RequiredParameterCount => 0;

    /// <inheritdoc/>
    public int? MaximumParameterCount => 1;

#pragma warning disable CS1998 // async method lacks await – intentional: body is synchronous
    /// <inheritdoc/>
    public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
    {
        var item = context.Arguments.Count > 0 ? context.Arguments[0] : null;
        foreach (var result in _body(item, _captureValues))
        {
            yield return result;
        }
    }
#pragma warning restore CS1998
}

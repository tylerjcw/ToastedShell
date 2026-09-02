using Tosh.Runtime;
using Tosh.Runtime.Formats;

namespace Tosh.Stdlib.Data;

/// <summary>
/// JSON, with `--typed` honoured on read as well as write — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wraps <see cref="JsonDataFormat"/> rather than replacing it: writing, compaction and every
/// untyped path are unchanged and still live in <c>Toast.Runtime</c> beside the other formats.
/// This type exists only to add the half that needs the program's declared types, which
/// <c>Toast.Runtime</c> cannot reach.
/// </para>
/// <para>
/// Without it `--typed` wrote a tag nothing read back — a promise made by the writer and kept by
/// nobody, which is worse than not tagging at all.
/// </para>
/// </remarks>
internal sealed class TypedJsonDataFormat : IContextualDataFormat
{
    private readonly JsonDataFormat _inner = new();

    public string Name => _inner.Name;

    public IReadOnlyList<string> Aliases => _inner.Aliases;

    public string Description => _inner.Description;

    public IAsyncEnumerable<object?> SerializeAsync(
        IReadOnlyList<object?> values,
        IReadOnlyList<object?> arguments) => _inner.SerializeAsync(values, arguments);

    public IAsyncEnumerable<object?> SerializeAsync(
        IReadOnlyList<object?> values,
        IReadOnlyList<object?> arguments,
        CommandContext context) => _inner.SerializeAsync(values, arguments);

    public IAsyncEnumerable<object?> DeserializeAsync(
        string text,
        IReadOnlyList<object?> arguments) => _inner.DeserializeAsync(text, arguments);

    public async IAsyncEnumerable<object?> DeserializeAsync(
        string text,
        IReadOnlyList<object?> arguments,
        CommandContext context)
    {
        var typed = ParsedCommandArguments.Parse(arguments).HasFlag("typed");

        await foreach (var value in _inner.DeserializeAsync(text, arguments))
        {
            yield return typed ? TypedValueRebuilder.Rebuild(value, context.ShellTypes) : value;
        }
    }
}

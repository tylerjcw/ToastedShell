namespace Tosh.Runtime.Formats;

/// <summary>
/// A format that needs to know about the running program, not just the text — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every other format in the registry is a pure text transform, which is why
/// <see cref="IDataFormat"/> offers no context. Tōast Object Notation is the first that is not:
/// it resolves names against the program's own declared types, deliberately, because that is
/// what makes a value recoverable *as itself* rather than as a bag of fields.
/// </para>
/// <para>
/// Additive on purpose. <c>from</c> and <c>to</c> prefer these overloads when a format
/// implements them and fall back otherwise, so the existing formats are untouched and a format
/// that needs no context never has to accept one.
/// </para>
/// </remarks>
public interface IContextualDataFormat : IDataFormat
{
    IAsyncEnumerable<object?> DeserializeAsync(
        string text,
        IReadOnlyList<object?> arguments,
        CommandContext context);

    IAsyncEnumerable<object?> SerializeAsync(
        IReadOnlyList<object?> values,
        IReadOnlyList<object?> arguments,
        CommandContext context);
}

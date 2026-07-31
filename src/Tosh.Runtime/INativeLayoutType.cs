namespace Tosh.Runtime;

/// <summary>
/// A TōSh-declared type that carries a native memory layout — implemented by
/// the <c>raw struct</c> façade.
///
/// The interop commands (<c>size-of</c>, <c>offset-of</c>, <c>alloc</c>,
/// <c>read-buffer</c>, <c>write-buffer</c>) accept a type by name, but a
/// qualified name like <c>Demo.Pair</c> is <em>evaluated</em> to the type object
/// before the command runs, so there is no name left to resolve. Recognising the
/// object directly is what makes bare and qualified forms behave the same.
/// </summary>
public interface INativeLayoutType
{
    /// <summary>The emitted sequential-layout CLR type.</summary>
    Type ClrType { get; }
}

namespace Tosh.Language.Bridge;

/// <summary>
/// Implemented by an emitted CLR subclass that forwards its virtuals to a tōsh object.
/// </summary>
/// <remarks>
/// <para>
/// The emitted type needs somewhere to keep the tōsh instance it dispatches to, and the
/// instance cannot be handed to the constructor: the base constructor runs first, and a
/// base constructor that calls a virtual — which plenty of .NET types do — would dispatch
/// before the field could possibly be set. So it is attached afterwards, and every emitted
/// override checks for it and calls <c>base</c> while it is absent.
/// </para>
/// <para>
/// That check is not only for construction. It is also what makes an emitted subclass safe
/// to hand to code that clones or deserialises it: a copy with no tōsh object behind it
/// behaves exactly like the base type rather than throwing.
/// </para>
/// </remarks>
public interface IToshClrBackedObject
{
    /// <summary>The tōsh object whose methods this type's overrides run, if attached.</summary>
    object? ToshInstance { get; set; }
}

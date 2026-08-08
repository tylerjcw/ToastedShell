namespace Tosh.Runtime;

/// <summary>
/// Resolves a ToastScript-declared type by name — a class, enum, struct, record, union,
/// interface or trait — including one nested inside another and one declared in a scope the
/// global registry never sees.
/// </summary>
/// <remarks>
/// <para>
/// A command holding only a <see cref="ToshRuntime"/> can reach declared types through
/// <c>runtime.Classes</c>, which is where a *top-level* declaration lands. That is not where they
/// all land: a declaration inside a script, a module, or a block registers in a lexical scope,
/// and a nested type is a static member of the class declaring it rather than an entry anywhere.
/// So <c>describe-type Fuel</c> answered "Unable to resolve type 'Fuel'" for an enum declared two
/// lines above it in the same script, and <c>cast Fuel $v</c> never reached a conversion at all
/// (<c>TS-P2-55</c>).
/// </para>
/// <para>
/// The engine implements this over the same <c>TryGetNamedType</c> that resolves a type name in
/// source, so a command and an annotation answer the same question the same way rather than by
/// two walks that will drift.
/// </para>
/// <para>
/// Resolution is live rather than snapshotted, unlike <see cref="IScopedCommandView"/>: a
/// command resolves a type name while the engine's scopes are still the caller's, and a command
/// that stored its context to resolve names later would be asking a question about a scope that
/// no longer exists.
/// </para>
/// </remarks>
public interface IShellNamedTypeView
{
    bool TryGetNamedType(string name, out IShellNamedType type);
}

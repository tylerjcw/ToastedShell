namespace Tosh.Runtime;

/// <summary>
/// Records the original tosh identifier of a metadata element
/// (type, method, field) whose CLR name was rewritten by the
/// emitter's name-mangling pass to keep the assembly consumable
/// from C-style languages. Tosh allows hyphens and other CLR-illegal
/// characters in identifiers (e.g. <c>func to-json</c>); the CLR
/// itself accepts those at the metadata level but every C-style
/// language compiler rejects them. The emitter rewrites such
/// names — replacing each illegal character with <c>_</c> and
/// prefixing leading digits — and stamps the original on the
/// resulting member with this attribute so downstream tooling
/// (LSP, MCP, debuggers, hover help) can present the user's
/// original spelling.
///
/// <para>
/// Only emitted when the mangling pass actually changes the name
/// (verbatim names that already form valid CLR identifiers are
/// not stamped, to keep metadata small).
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct |
    AttributeTargets.Enum |
    AttributeTargets.Method | AttributeTargets.Field |
    AttributeTargets.Property | AttributeTargets.Constructor,
    AllowMultiple = false)]
public sealed class ToshOriginalNameAttribute : Attribute
{
    public ToshOriginalNameAttribute(string originalName)
    {
        OriginalName = originalName;
    }

    public string OriginalName { get; }
}

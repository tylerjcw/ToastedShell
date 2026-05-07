namespace Tosh.Runtime;

/// <summary>
/// Stamped at the assembly level by the TōSh emitter to declare
/// which version of the public CLR ABI a tosh-compiled assembly
/// conforms to.
///
/// <para>
/// Cross-language consumers (C#, F#, VB, native interop, tooling)
/// can read this attribute via reflection to detect whether they
/// can rely on a given ABI promise. The value is a single major
/// integer; no minor / patch components, by design — backwards-
/// compatible additions don't change the version, breaking
/// changes bump it (and ship a new attribute per the spec's
/// compatibility-policy section).
/// </para>
///
/// <para>
/// v1 is the contract documented in <c>docs/CLR_ABI_v1.md</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ToshAbiAttribute : Attribute
{
    public ToshAbiAttribute(int version)
    {
        Version = version;
    }

    public int Version { get; }
}

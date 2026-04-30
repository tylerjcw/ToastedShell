namespace Tosh.Core;

/// <summary>
/// Marks a concrete <see cref="ShellCommand"/> subclass as deliberately not registered in
/// the default engine command registry. Use sparingly — registry parity tests will skip any
/// type bearing this attribute.
/// </summary>
/// <remarks>
/// Typical use cases:
/// <list type="bullet">
///   <item>Commands that are only registered conditionally (e.g., test fixtures, plugins).</item>
///   <item>Commands that are spawned per-invocation by a parent command rather than registered globally.</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class IntentionallyUnregisteredAttribute : Attribute
{
    public IntentionallyUnregisteredAttribute(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

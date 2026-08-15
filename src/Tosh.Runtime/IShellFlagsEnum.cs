namespace Tosh.Runtime;

/// <summary>
/// An enum declaration that runtime operator dispatch can combine bits into.
/// </summary>
/// <remarks>
/// `TS-P3-14`. Implemented by <c>Tosh.Language.ToshEnumDefinition</c> so the
/// bitwise operators can build a member out of a combined value without the
/// runtime depending on the language assembly — the same separation
/// <see cref="IShellEnumValue"/> already makes for ordering.
/// </remarks>
public interface IShellFlagsEnum
{
    /// <summary>The enum's name, used to refuse combining two different enums.</summary>
    string ShellTypeName { get; }

    /// <summary>
    /// Whether the declaration said <c>flags</c>, and so whether a combination of
    /// members is itself a meaningful member.
    /// </summary>
    bool IsFlags { get; }

    /// <summary>
    /// The member standing for <paramref name="value"/>'s bits.
    /// </summary>
    /// <remarks>
    /// Called only when <see cref="IsFlags"/> is true. An enum that did not
    /// declare itself combinable has no member for a combination, and saying so
    /// with a number is more honest than inventing one.
    /// </remarks>
    object FromFlags(long value);
}

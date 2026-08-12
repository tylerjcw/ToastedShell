namespace Tosh.Runtime;

/// <summary>
/// The name a diagnostic should call a value's type — <c>TS-P2-18</c>.
/// </summary>
/// <remarks>
/// <para>
/// User-facing messages named the CLR implementation type, so a missing member on a class
/// instance reported "was not found on type 'Tosh.Language.ToshClassInstance'" and an enum
/// comparison reported "'Tosh.Language.ToshEnumValue'". Those names belong to the shell's
/// internals: the reader wrote <c>class S</c> and <c>enum E</c>, has no way to act on
/// <c>ToshClassInstance</c>, and <c>type-of</c> had been answering <c>S</c> all along — so the
/// diagnostics disagreed with the shell's own introspection.
/// </para>
/// <para>
/// One rule in one place, because the leak was spread across the member accessor, the operator
/// evaluator and the conversion paths, and each had its own spelling of "name this type".
/// </para>
/// </remarks>
public static class ShellTypeNaming
{
    /// <summary>The shell-level name of <paramref name="value"/>'s type.</summary>
    /// <remarks>
    /// Falls back to the CLR display name for an ordinary object, which is the right answer for
    /// a genuine CLR value — <c>System.String</c> should still read as <c>String</c>.
    /// </remarks>
    public static string Describe(object? value) => value switch
    {
        null => "null",
        IShellTypedObject typed => typed.ShellTypeDescriptor.ShellTypeName,
        IShellTypeDescriptor descriptor => descriptor.ShellTypeName,
        _ => Describe(value.GetType()),
    };

    /// <summary>The shell-level name of <paramref name="type"/>, when it has one.</summary>
    /// <remarks>
    /// A <see cref="Type"/> alone cannot carry the shell name of the *instance* it describes —
    /// every ToastScript class shares <c>ToshClassInstance</c> — so this answers the CLR display
    /// name and callers holding a value should prefer <see cref="Describe(object?)"/>.
    /// </remarks>
    public static string Describe(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ReflectionMetadataUtilities.GetDisplayName(type);
    }
}

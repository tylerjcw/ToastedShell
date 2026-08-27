namespace Tosh.Runtime;

/// <summary>
/// Converts a value to a ToastScript-declared type, the way <see cref="TypeConversion"/> does for
/// CLR types.
/// </summary>
/// <remarks>
/// <para>
/// `TS-P2-55`. Casting an enum member *to* a number worked; casting a number *to* an enum member
/// did not, because <c>cast</c> resolved its target through CLR type lookup alone and a name
/// declared in ToastScript is never a CLR type. The conversion path was never reached at all.
/// </para>
/// <para>
/// Only two conversions exist here, deliberately. An enum converts from its member name or its
/// backing value, which is the reported case and the symmetric partner of the conversion that
/// already worked. Everything else converts only from itself — a value that is already an
/// instance of the target passes through, and anything else is refused. <c>cast</c> is not a
/// constructor, and inventing "convert this record into that class" semantics is a language
/// decision rather than a repair.
/// </para>
/// </remarks>
public static class ShellTypeConversion
{
    /// <summary>
    /// Converts <paramref name="value"/> to <paramref name="target"/>, or explains why not.
    /// </summary>
    /// <param name="reason">
    /// Set when conversion fails, phrased to complete "Could not cast … because …".
    /// </param>
    public static bool TryConvert(
        object? value,
        IShellTypeDescriptor target,
        out object? converted,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(target);

        converted = null;
        reason = string.Empty;

        // Already the right type — including a subclass, which `IsInstanceOfShellType` decides
        // by the same walk `is` uses, so `cast` and `is` cannot come to disagree.
        if (value is not null && OperatorEvaluator.IsInstanceOfShellType(value, target.ShellTypeName))
        {
            converted = value;
            return true;
        }

        if (target.ShellIsEnum && target is IShellStaticType enumType)
        {
            return TryConvertToEnum(value, target, enumType, out converted, out reason);
        }

        if (value is null)
        {
            reason = $"null is not a '{target.ShellTypeName}'";
            return false;
        }

        reason = $"'{target.ShellTypeName}' is a declared type, and cast converts only a value that already is one";
        return false;
    }

    private static bool TryConvertToEnum(
        object? value,
        IShellTypeDescriptor target,
        IShellStaticType enumType,
        out object? converted,
        out string reason)
    {
        converted = null;
        reason = string.Empty;

        var members = target.GetShellMembers();

        if (value is string or ShellTextLine)
        {
            var name = value is ShellTextLine line ? line.Text : (string)value;

            foreach (var member in members)
            {
                if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    enumType.TryGetStaticMember(member.Name, out var byName))
                {
                    converted = byName;
                    return true;
                }
            }

            reason = $"'{target.ShellTypeName}' has no member named '{name}' ({DescribeMembers(members)})";
            return false;
        }

        // A backing value. Compared after conversion to the member's own underlying type, so a
        // `long` literal matches an `int`-backed member rather than missing it on boxed identity.
        foreach (var member in members)
        {
            if (!enumType.TryGetStaticMember(member.Name, out var candidate) ||
                candidate is not IShellEnumValue enumValue ||
                enumValue.UnderlyingValue is null)
            {
                continue;
            }

            if (!TypeConversion.TryConvert(value, enumValue.UnderlyingValue.GetType(), out var normalized))
            {
                continue;
            }

            if (Equals(normalized, enumValue.UnderlyingValue))
            {
                converted = candidate;
                return true;
            }
        }

        reason = value is null
            ? $"null matches no member of '{target.ShellTypeName}'"
            : $"no member of '{target.ShellTypeName}' has the value '{value}' ({DescribeMembers(members)})";
        return false;
    }

    private static string DescribeMembers(IReadOnlyList<ShellMemberDescriptor> members)
    {
        return members.Count == 0
            ? "it declares no members"
            : "members: " + string.Join(", ", members.Select(member => member.Name));
    }
}

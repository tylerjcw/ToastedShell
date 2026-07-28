using System.Globalization;

namespace Tosh.Runtime;

/// <summary>
/// Shared comparison helpers for shell enum members (TS-P1-15). Kept in
/// <c>Tosh.Runtime</c> so both the enum value type and the operator
/// evaluator use one rule for reducing a member to its backing value.
/// </summary>
public static class ShellEnumComparison
{
    /// <summary>
    /// Unwraps an enum member to the value comparisons should use. A
    /// non-enum value is returned unchanged, so callers can pass either
    /// operand through this before comparing.
    /// </summary>
    public static object? Unwrap(object? value)
        => value is IShellEnumValue enumValue ? enumValue.UnderlyingValue : value;

    /// <summary>
    /// Compares two backing values numerically when both are numeric,
    /// falling back to <see cref="IComparable"/> and finally to ordinal
    /// text comparison so members always have a deterministic order.
    /// </summary>
    public static int CompareUnderlying(object? left, object? right)
    {
        left = Unwrap(left);
        right = Unwrap(right);

        if (left is null || right is null)
        {
            return left is null && right is null ? 0 : left is null ? -1 : 1;
        }

        if (TryToDecimal(left, out var leftNumber) && TryToDecimal(right, out var rightNumber))
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (left is IComparable comparable && left.GetType() == right.GetType())
        {
            return comparable.CompareTo(right);
        }

        return string.CompareOrdinal(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// True when an enum member and the other operand denote the same
    /// value: either the same member of the same enum, or a member whose
    /// backing value equals the other operand.
    /// </summary>
    public static bool AreEquivalent(object? left, object? right)
    {
        if (left is IShellEnumValue leftEnum && right is IShellEnumValue rightEnum)
        {
            return string.Equals(leftEnum.EnumTypeName, rightEnum.EnumTypeName, StringComparison.Ordinal)
                && string.Equals(leftEnum.Name, rightEnum.Name, StringComparison.Ordinal);
        }

        var unwrappedLeft = Unwrap(left);
        var unwrappedRight = Unwrap(right);

        if (unwrappedLeft is null || unwrappedRight is null)
        {
            return unwrappedLeft is null && unwrappedRight is null;
        }

        if (TryToDecimal(unwrappedLeft, out var leftNumber) && TryToDecimal(unwrappedRight, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return Equals(unwrappedLeft, unwrappedRight);
    }

    private static bool TryToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            case float or double:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    result = default;
                    return false;
                }
                result = (decimal)d;
                return true;
            default:
                result = default;
                return false;
        }
    }
}

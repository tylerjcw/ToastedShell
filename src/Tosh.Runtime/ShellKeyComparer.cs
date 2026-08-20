using System.Collections;

namespace Tosh.Runtime;

/// <summary>
/// Key equality: the relation containers use — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// <c>==</c> is coercive, so <c>1 == "1"</c> holds. That is what a shell wants from a
/// comparison and it is unusable as a container relation, because coercion makes equality
/// **intransitive**: <c>"1" == 1</c> and <c>1 == "1.0"</c> are both true while
/// <c>"1" == "1.0"</c> is false. A relation with no equivalence classes has nothing for a
/// hash table to put in a bucket, and a container built on it answers differently
/// depending on the order values arrived in — which a dictionary here demonstrably did:
/// two dictionaries holding the same two keys returned different values for the same
/// lookup.
/// </para>
/// <para>
/// This relation asks whether two values are <em>the same value</em>. It never converts
/// across types, so it is transitive, and a hash consistent with it is well defined.
/// Numbers are the exception that proves the rule: width is not part of a number's
/// identity, so <c>1</c>, <c>1L</c> and <c>1.0</c> are one key, compared exactly by the
/// same rule <c>OperatorEvaluator</c> uses.
/// </para>
/// </remarks>
public sealed class ShellKeyComparer : IEqualityComparer<object?>
{
    public static readonly ShellKeyComparer Instance = new();

    private ShellKeyComparer() { }

    public new bool Equals(object? x, object? y) => AreSameKey(x, y, depth: 0);

    public int GetHashCode(object? obj) => ComputeHash(obj, depth: 0);

    /// <summary>How deep a structural key may nest before it stops descending.</summary>
    /// <remarks>
    /// A cycle would otherwise recurse forever. Beyond the limit two values compare by
    /// reference and hash to a constant, which is correct — never a wrong answer, only a
    /// slower bucket — rather than a stack overflow.
    /// </remarks>
    private const int MaximumDepth = 16;

    private static bool AreSameKey(object? x, object? y, int depth)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        if (depth >= MaximumDepth)
        {
            return false;
        }

        // Width is not part of a number's identity, but a string's textual resemblance to
        // a number is not identity at all: `1` and `"1"` are different keys.
        if (TryAsNumber(x, out var leftNumber) && TryAsNumber(y, out var rightNumber))
        {
            return NumbersAreSame(leftNumber, rightNumber);
        }

        if (x is string leftText)
        {
            return y is string rightText && string.Equals(leftText, rightText, StringComparison.Ordinal);
        }

        if (y is string)
        {
            return false;
        }

        if (x is bool leftBool)
        {
            return y is bool rightBool && leftBool == rightBool;
        }

        // An enum member keys with its own enum's members, not with its backing number.
        if (x is IShellEnumValue leftEnum || y is IShellEnumValue)
        {
            return x is IShellEnumValue left && y is IShellEnumValue right &&
                   string.Equals(left.EnumTypeName, right.EnumTypeName, StringComparison.Ordinal) &&
                   ShellEnumComparison.AreEquivalent(x, y);
        }

        // A type that defines its own equality decides. This has to precede the record
        // branch, because a class instance is `IShellRecordObject` and therefore
        // record-*like*: without this, two separate instances of a class holding equal
        // properties compared structurally and folded together, when a class with no
        // declared `equals` is a key only to itself.
        if (DeclaresOwnEquality(x.GetType()) || DeclaresOwnEquality(y.GetType()))
        {
            return x.Equals(y);
        }

        // Records and dictionaries: same names and values, in any order. This is the case
        // the JSON-string key got wrong — it preserved field order, so two records `==`
        // called equal survived `distinct` as separate values.
        if (ShellRecordUtilities.IsRecordLike(x) || ShellRecordUtilities.IsRecordLike(y))
        {
            return ShellRecordUtilities.IsRecordLike(x) && ShellRecordUtilities.IsRecordLike(y) &&
                   RecordsAreSameKey(x, y, depth);
        }

        if (x is IEnumerable leftSequence && y is IEnumerable rightSequence)
        {
            return SequencesAreSameKey(leftSequence, rightSequence, depth);
        }

        // A class instance uses its own `equals` when it declares one, and is otherwise a
        // key only to itself. Both are already true of `Equals`: `ToshClassInstance`
        // overrides it to dispatch a declared `equals` and to fall back to reference
        // identity, so this needs no separate protocol.
        return x.Equals(y);
    }

    private static bool RecordsAreSameKey(object x, object y, int depth)
    {
        if (!ShellRecordUtilities.TryGetFields(x, out var left) ||
            !ShellRecordUtilities.TryGetFields(y, out var right) ||
            left.Count != right.Count)
        {
            return false;
        }

        // Matched by name, so field order does not participate.
        var rightByName = new Dictionary<string, object?>(right.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var field in right)
        {
            rightByName[field.Key] = field.Value;
        }

        foreach (var field in left)
        {
            if (!rightByName.TryGetValue(field.Key, out var other) ||
                !AreSameKey(field.Value, other, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequencesAreSameKey(IEnumerable x, IEnumerable y, int depth)
    {
        var leftEnumerator = x.GetEnumerator();
        var rightEnumerator = y.GetEnumerator();

        try
        {
            while (true)
            {
                var leftHasNext = leftEnumerator.MoveNext();
                var rightHasNext = rightEnumerator.MoveNext();

                if (leftHasNext != rightHasNext)
                {
                    return false;
                }

                if (!leftHasNext)
                {
                    return true;
                }

                if (!AreSameKey(leftEnumerator.Current, rightEnumerator.Current, depth + 1))
                {
                    return false;
                }
            }
        }
        finally
        {
            (leftEnumerator as IDisposable)?.Dispose();
            (rightEnumerator as IDisposable)?.Dispose();
        }
    }

    private static int ComputeHash(object? value, int depth)
    {
        if (value is null)
        {
            return 0;
        }

        if (depth >= MaximumDepth)
        {
            return 0;
        }

        // Every number that could be the same key must hash alike, so an integral value
        // hashes as an integer whatever width it arrived in.
        if (TryAsNumber(value, out var number))
        {
            return number.IsIntegral
                ? number.Integer.GetHashCode()
                : number.Floating.GetHashCode();
        }

        if (value is string text)
        {
            return text.GetHashCode(StringComparison.Ordinal);
        }

        if (value is bool flag)
        {
            return flag.GetHashCode();
        }

        if (value is IShellEnumValue enumValue)
        {
            return HashCode.Combine(enumValue.EnumTypeName, enumValue.ToString());
        }

        if (DeclaresOwnEquality(value.GetType()))
        {
            return value.GetHashCode();
        }

        if (ShellRecordUtilities.IsRecordLike(value))
        {
            // Order-independent: the fields are combined with a commutative operation, so
            // two records holding the same pairs hash alike whatever order they were
            // written in. That is the whole point — an order-sensitive key is what made
            // `distinct` keep two records `==` called equal.
            var accumulator = 0;

            if (ShellRecordUtilities.TryGetFields(value, out var fields))
            {
                foreach (var field in fields)
                {
                    accumulator ^= HashCode.Combine(
                        field.Key.GetHashCode(StringComparison.OrdinalIgnoreCase),
                        ComputeHash(field.Value, depth + 1));
                }
            }

            return accumulator;
        }

        if (value is IEnumerable sequence)
        {
            var hash = new HashCode();

            foreach (var element in sequence)
            {
                hash.Add(ComputeHash(element, depth + 1));
            }

            return hash.ToHashCode();
        }

        // As with `Equals` above, `ToshClassInstance.GetHashCode` already dispatches a
        // declared `hash`, and already answers a constant for a class that declares
        // `equals` without one — so the two stay consistent without a protocol here.
        return value.GetHashCode();
    }

    /// <summary>
    /// Whether a type overrides <see cref="object.Equals(object?)"/> — that is, whether it
    /// has an opinion about its own identity that this comparer must not override.
    /// </summary>
    private static bool DeclaresOwnEquality(Type type) =>
        type.GetMethod(nameof(Equals), [typeof(object)])?.DeclaringType != typeof(object);

    private readonly record struct NumericKey(bool IsIntegral, long Integer, double Floating);

    private static bool TryAsNumber(object value, out NumericKey number)
    {
        switch (value)
        {
            case sbyte v: number = new(true, v, v); return true;
            case byte v: number = new(true, v, v); return true;
            case short v: number = new(true, v, v); return true;
            case ushort v: number = new(true, v, v); return true;
            case int v: number = new(true, v, v); return true;
            case uint v: number = new(true, v, v); return true;
            case long v: number = new(true, v, v); return true;
            case ulong v when v <= long.MaxValue: number = new(true, (long)v, v); return true;

            case float v: return TryFromDouble(v, out number);
            case double v: return TryFromDouble(v, out number);
            case decimal v when decimal.Truncate(v) == v && v >= long.MinValue && v <= long.MaxValue:
                number = new(true, (long)v, (double)v);
                return true;
            case decimal v: number = new(false, 0, (double)v); return true;

            default: number = default; return false;
        }
    }

    private static bool TryFromDouble(double value, out NumericKey number)
    {
        // An integral double inside `long`'s range keys as that integer, so `1.0` and `1`
        // land together. Anything else keys as itself; `NaN` included, which keys with
        // other `NaN`s because equality here is reflexive.
        if (!double.IsNaN(value) && !double.IsInfinity(value) &&
            Math.Floor(value) == value &&
            value >= -9.2233720368547758E18 && value < 9.2233720368547758E18)
        {
            number = new(true, (long)value, value);
            return true;
        }

        number = new(false, 0, value);
        return true;
    }

    private static bool NumbersAreSame(NumericKey left, NumericKey right)
    {
        if (left.IsIntegral != right.IsIntegral)
        {
            return false;
        }

        return left.IsIntegral
            ? left.Integer == right.Integer
            : left.Floating.Equals(right.Floating);   // `Equals`, so NaN keys with NaN.
    }
}

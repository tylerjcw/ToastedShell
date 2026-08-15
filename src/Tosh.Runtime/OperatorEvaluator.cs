using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Tosh.Runtime.Units;

namespace Tosh.Runtime;

public static class OperatorEvaluator
{
    /// <summary>
    /// Optional host hook for resolving named trait-style type constraints used by the
    /// `is`/`is-not` operators (e.g. `$x is Numeric`). Receives the constraint name and
    /// the value's CLR <see cref="Type"/>; returns <c>true</c> when the type satisfies it.
    /// When unset (or returns <c>false</c>), <see cref="IsType"/> falls back to the
    /// built-in alias table and per-type matching.
    /// </summary>
    public static Func<string, Type, bool>? ResolveTraitConstraint { get; set; }

    public static object? EvaluateUnary(string @operator, object? operand)
    {
        return @operator switch
        {
            "!" or "not" => !ToBoolean(operand),

            // `TS-P2-02`. Unary `-` and `+` were accepted by the parser
            // (`IsUnaryOperatorToken` names both) and implemented by nobody, so
            // `- $x` reached here and reported "Unsupported unary operator '-'".
            // Expressed through `Subtract` and `Add` rather than as a second numeric
            // tower: every widening, unit and shell-numeric rule those already carry —
            // vectors, matrices, complex, storage sizes — applies unchanged, and an
            // operand that cannot be negated reports the type message they already give
            // instead of a message about the operator.
            "-" => operand is Quantity quantity ? -quantity : Subtract(0, operand),
            "+" => operand is Quantity quantity ? quantity : Add(0, operand),

            "bnot" => Bitwise(operand, operand, "bnot", (a, _) => ~a),

            _ => throw new InvalidOperationException($"Unsupported unary operator '{@operator}'."),
        };
    }

    public static object? EvaluateBinary(object? left, string @operator, object? right)
    {
        if (TryInvokeShellBinaryOperator(left, @operator, right, out var leftResult))
        {
            return leftResult;
        }

        if (TryInvokeShellBinaryOperator(right, @operator, left, out var rightResult))
        {
            return rightResult;
        }

        // Auto-unwrap ShellTextLine so operators work transparently on text content.
        if (left is ShellTextLine leftLine) left = leftLine.Text;
        if (right is ShellTextLine rightLine) right = rightLine.Text;

        return @operator switch
        {
            "+" => Add(left, right),
            "-" => Subtract(left, right),
            "*" => Multiply(left, right),
            "/" => Divide(left, right),
            "//" => FloorDivide(left, right),
            "%" => Modulo(left, right),
            "**" => Power(left, right),
            "==" => AreEqual(left, right),
            "!=" => !AreEqual(left, right),
            "=~" => RegexMatch(left, right),
            "!~" => !RegexMatch(left, right),
            "in" => IsIn(left, right),
            "not-in" => !IsIn(left, right),
            ">" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison > 0),
            ">=" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison >= 0),
            "<" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison < 0),
            "<=" => EvaluateOrderedComparison(left, right, nullable: true, comparison => comparison <= 0),
            "contains" => Contains(left, right),
            "starts-with" => StartsWith(left, right),
            "ends-with" => EndsWith(left, right),
            "is" => IsType(left, right),
            "is-not" => !IsType(left, right),
            "as" => CastAs(left, right),
            "is-in" => IsIn(left, right),
            "is-not-in" => !IsIn(left, right),
            "band" => Bitwise(left, right, "band", (a, b) => a & b),
            "bor" => Bitwise(left, right, "bor", (a, b) => a | b),
            "bxor" => Bitwise(left, right, "bxor", (a, b) => a ^ b),
            "shl" => Shift(left, right, "shl", (a, n) => a << n),
            "shr" => Shift(left, right, "shr", (a, n) => a >> n),
            "has" => HasFlag(left, right),
            "and" => ToBoolean(left) && ToBoolean(right),
            "or" => ToBoolean(left) || ToBoolean(right),
            "=" => throw new InvalidOperationException("Assignment operations require a variable."),
            _ => throw new InvalidOperationException($"Unsupported operator '{@operator}'."),
        };
    }

    /// <summary>
    /// Evaluates an eager binary expression and attaches the canonical
    /// structured source diagnostic to ordinary operator failures. User
    /// throws, cancellation, control flow, defer failures, and diagnostics
    /// preserve their identity.
    /// </summary>
    public static object? EvaluateBinaryWithDiagnostics(
        object? left,
        string @operator,
        object? right,
        string sourceName,
        string sourceText,
        int spanStart,
        int spanLength)
    {
        try
        {
            return EvaluateBinary(left, @operator, right);
        }
        catch (Exception exception) when (MustPreserveExpressionFailure(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: exception is InvalidOperationException
                    ? "tosh.runtime.expression_failed"
                    : "tosh.runtime.unexpected_exception",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: new TextSpan(spanStart, spanLength),
                Label: "while evaluating this expression"));
        }
    }

    private static bool TryInvokeShellBinaryOperator(
        object? instance,
        string @operator,
        object? other,
        out object? result)
    {
        result = null;
        if (instance is IShellBinaryOperatorObject shellOperator)
        {
            return shellOperator.TryEvaluateBinaryOperator(
                @operator,
                other,
                out result);
        }

        return TryInvokeCompiledSpecialMethod(
            instance,
            @operator,
            [other],
            out result);
    }

    private static bool TryInvokeCompiledSpecialMethod(
        object? instance,
        string methodName,
        object?[] arguments,
        out object? result)
    {
        result = null;
        if (instance is null ||
            instance.GetType().GetCustomAttribute<ToshTypeAttribute>() is null)
        {
            return false;
        }

        MethodInfo? method = null;
        for (var current = instance.GetType();
             current is not null &&
             current.GetCustomAttribute<ToshTypeAttribute>() is not null;
             current = current.BaseType)
        {
            method = current
                .GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate =>
                    candidate.GetParameters().Length == arguments.Length &&
                    string.Equals(
                        candidate
                            .GetCustomAttribute<ToshOriginalNameAttribute>()
                            ?.OriginalName ?? candidate.Name,
                        methodName,
                        StringComparison.Ordinal));
            if (method is not null)
            {
                break;
            }
        }

        if (method is null)
        {
            return false;
        }

        try
        {
            result = method.Invoke(instance, arguments);
            return true;
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static bool MustPreserveExpressionFailure(Exception exception) =>
        exception is ToshDiagnosticException or
            OperationCanceledException or
            ShellControlFlowException or
            ThrowSignalException ||
        ToshDeferFailures.IsDeferFailure(exception) ||
        exception.Data.Contains("tosh.thrown");

    private static string ToOperatorString(object? value) =>
        ToOperatorString(
            value,
            new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static string ToOperatorString(
        object? value,
        HashSet<object> activeValues)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var type = value.GetType();
        if (type.GetCustomAttribute<ToshTypeAttribute>() is null)
        {
            return value.ToString() ?? string.Empty;
        }

        var fallbackName =
            type.GetCustomAttribute<ToshOriginalNameAttribute>()?.OriginalName
            ?? type.Name;
        if (!activeValues.Add(value))
        {
            return fallbackName;
        }

        try
        {
            if (TryInvokeCompiledSpecialMethod(
                    value,
                    nameof(ToString),
                    Array.Empty<object?>(),
                    out var converted))
            {
                return ToOperatorString(converted, activeValues);
            }

            return fallbackName;
        }
        finally
        {
            activeValues.Remove(value);
        }
    }

    public static bool Matches(object? actual, string @operator, object? expected, bool nullable)
    {
        if (actual is ShellTextLine actualLine) actual = actualLine.Text;
        if (expected is ShellTextLine expectedLine) expected = expectedLine.Text;

        return @operator switch
        {
            "==" => AreEqual(actual, expected),
            "!=" => !AreEqual(actual, expected),
            "=~" => RegexMatch(actual, expected),
            "!~" => !RegexMatch(actual, expected),
            "in" => IsIn(actual, expected),
            "not-in" => !IsIn(actual, expected),
            "contains" => Contains(actual, expected),
            "starts-with" => StartsWith(actual, expected),
            "ends-with" => EndsWith(actual, expected),
            ">" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison > 0),
            ">=" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison >= 0),
            "<" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison < 0),
            "<=" => EvaluateOrderedComparison(actual, expected, nullable, comparison => comparison <= 0),
            "is" => IsType(actual, expected),
            "is-not" => !IsType(actual, expected),
            _ => throw new InvalidOperationException($"Unsupported operator '{@operator}'. Supported operators: ==, !=, =~, !~, in, not-in, >, >=, <, <=, contains, starts-with, ends-with, is, is-not."),
        };
    }

    public static bool AreEqual(object? actual, object? expected)
    {
        // Records and dictionaries compare by name and value, never by enumeration
        // order (TS-P1-10). This has to precede the element-wise path below, because an
        // ExpandoObject is an IEnumerable<KeyValuePair<…>> and would otherwise be
        // compared as an *ordered sequence* — which made
        // `{| a = 1, b = 2 |} == {| b = 2, a = 1 |}` false while the same record written
        // twice in the same order compared true. Insertion order is not part of a
        // record's identity.
        if (TryCompareByName(actual, expected, out var byName))
        {
            return byName;
        }

        // When both sides are non-string enumerables, compare element-wise.
        if (actual is IEnumerable actualCollection && actual is not string &&
            expected is IEnumerable expectedCollection && expected is not string)
        {
            var actualEnumerator = actualCollection.GetEnumerator();
            var expectedEnumerator = expectedCollection.GetEnumerator();

            try
            {
                while (true)
                {
                    var actualHasNext = actualEnumerator.MoveNext();
                    var expectedHasNext = expectedEnumerator.MoveNext();

                    if (!actualHasNext && !expectedHasNext)
                    {
                        return true;
                    }

                    if (actualHasNext != expectedHasNext)
                    {
                        return false;
                    }

                    if (!AreEqual(actualEnumerator.Current, expectedEnumerator.Current))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                (actualEnumerator as IDisposable)?.Dispose();
                (expectedEnumerator as IDisposable)?.Dispose();
            }
        }

        if (actual is null || expected is null)
        {
            return Equals(actual, expected);
        }

        // TS-P1-15: an enum member equals another member of the same
        // enum, and equals its own backing value, so `E.Mid == 1` holds
        // for a numeric-backed enum. Member-name comparison against a
        // string still flows through the ordinary conversion path below.
        if ((actual is IShellEnumValue || expected is IShellEnumValue) &&
            actual is not string && expected is not string &&
            ShellEnumComparison.AreEquivalent(actual, expected))
        {
            return true;
        }

        // TS-P1-26: both directions are attempted, and equality holds if either
        // matches. Returning on the first *conversion* that succeeded — rather
        // than the first that produced equal values — made equality asymmetric:
        // `"true" == true` converted the bool to "True", compared it against
        // "true", and returned false without ever trying string-to-bool, while
        // `true == "true"` converted the other way and matched. Since the same
        // two conversions are attempted whichever operand comes first, testing
        // both makes the result independent of operand order by construction.
        var expectedIsConvertible =
            TypeConversion.TryConvert(expected, actual.GetType(), out var convertedExpected);
        if (expectedIsConvertible && ObjectEquals(actual, convertedExpected))
        {
            return true;
        }

        var actualIsConvertible =
            TypeConversion.TryConvert(actual, expected.GetType(), out var convertedActual);
        if (actualIsConvertible && ObjectEquals(convertedActual, expected))
        {
            return true;
        }

        // A successful conversion decides the answer, even when it produced
        // unequal values. Both directions are tried first, which is what makes
        // the result independent of operand order (TS-P1-26) — but falling
        // through to the tail would dispatch a user-defined `Equals` with an
        // operand of the wrong type, which the original single early return had
        // shielded against by accident.
        if (expectedIsConvertible || actualIsConvertible)
        {
            return false;
        }

        // TS-P1-14: no ToString()-based fallback. It previously made
        // mixed-type equality case-insensitive while string-to-string
        // equality stayed case-sensitive, so `E.Low == "LOW"` was true
        // while `"ABC" == "abc"` was false. Values with no convertible
        // shared representation are simply unequal; compare a value's
        // text form explicitly when that is the intent.
        return ObjectEquals(actual, expected);
    }

    private static bool ObjectEquals(object? actual, object? expected)
    {
        if (ReferenceEquals(actual, expected))
        {
            return true;
        }
        if (actual is null || expected is null)
        {
            return false;
        }

        if (TryInvokeCompiledSpecialMethod(
                actual,
                nameof(Equals),
                [expected],
                out var equality))
        {
            return ToBoolean(equality);
        }

        var type = actual.GetType();
        var toshType = type.GetCustomAttribute<ToshTypeAttribute>();
        if (toshType is not null &&
            string.Equals(toshType.Kind, "record", StringComparison.Ordinal) &&
            expected.GetType() == type)
        {
            foreach (var field in type.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                if (!AreEqual(field.GetValue(actual), field.GetValue(expected)))
                {
                    return false;
                }
            }

            return true;
        }

        return actual.Equals(expected);
    }

    public static bool EvaluateOrderedComparison(object? actual, object? expected, bool nullable, Func<int, bool> predicate)
    {
        if (actual is null || expected is null)
        {
            if (nullable)
            {
                return false;
            }

            throw new InvalidOperationException("Ordered comparisons require non-null values.");
        }

        // TS-P1-14: ordering is strict and symmetric. Booleans have no
        // meaningful order, and a string orders only against another
        // string — otherwise `"10" < 9` would silently pick lexicographic
        // ordering and answer true.
        if (actual is bool || expected is bool)
        {
            throw new InvalidOperationException(
                $"Values of type '{DescribeOperandType(actual)}' and '{DescribeOperandType(expected)}' cannot be ordered. Booleans have no ordering; compare them with '==' or use 'and'/'or'.");
        }

        if (actual is string != expected is string)
        {
            throw new InvalidOperationException(
                $"Values of type '{DescribeOperandType(actual)}' and '{DescribeOperandType(expected)}' cannot be ordered. Convert one operand explicitly, for example with 'cast'.");
        }

        // TS-P1-15: enum members order by their backing value, both
        // against each other and against a plain number. Two different
        // enums are not one ordered domain, though — members of `E` and
        // `F` both starting at 0 must not silently rank against each
        // other.
        if (actual is IShellEnumValue || expected is IShellEnumValue)
        {
            if (actual is IShellEnumValue leftEnum &&
                expected is IShellEnumValue rightEnum &&
                !string.Equals(leftEnum.EnumTypeName, rightEnum.EnumTypeName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Enum members of '{leftEnum.EnumTypeName}' and '{rightEnum.EnumTypeName}' cannot be ordered against each other.");
            }

            return predicate(ShellEnumComparison.CompareUnderlying(actual, expected));
        }

        // Convert in either direction so the same operand pair behaves
        // identically however it is written: `a < b` and `b > a` must
        // agree instead of one succeeding and the other failing.
        if (actual is IComparable comparable &&
            TypeConversion.TryConvert(expected, actual.GetType(), out var convertedExpected))
        {
            return predicate(comparable.CompareTo(convertedExpected));
        }

        if (expected is IComparable &&
            TypeConversion.TryConvert(actual, expected.GetType(), out var convertedActual) &&
            convertedActual is IComparable comparableActual)
        {
            return predicate(comparableActual.CompareTo(expected));
        }

        throw new InvalidOperationException(
            $"Values of type '{DescribeOperandType(actual)}' cannot be compared with '{DescribeOperandType(expected)}'.");
    }

    /// <summary>
    /// Names an operand's type the way the shell does — <c>TS-P2-18</c>.
    /// </summary>
    /// <remarks>
    /// Reported the CLR implementation type, so comparing an enum member said
    /// <c>'Tosh.Language.ToshEnumValue'</c> — a name the reader never wrote and cannot act on,
    /// while <c>type-of</c> answered <c>E</c> for the same value.
    /// </remarks>
    private static string DescribeOperandType(object? value) => ShellTypeNaming.Describe(value);

    /// <summary>
    /// Compares two record-like values field by field, ignoring order. Answers
    /// <see langword="false"/> from <paramref name="handled"/> when the pair is not two
    /// record-likes, so the caller falls through to its ordinary paths.
    /// </summary>
    /// <remarks>
    /// Keys are matched case-insensitively because member lookup is: a record's
    /// <c>TryGetMember</c> resolves <c>Name</c> for <c>name</c>, so equality that
    /// distinguished them would contradict access. A record is only compared against
    /// another record — mixing a record with a dict is left to the existing paths rather
    /// than declared equal here, since they are different shell types.
    /// </remarks>
    internal static bool TryCompareByName(object? actual, object? expected, out bool result)
    {
        result = false;

        // Deliberately narrower than IsRecordLike: string-keyed records only. A `{% … %}`
        // dictionary is object-keyed, and calling TryGetFields on one throws — it
        // iterates a generic Dictionary as DictionaryEntry, which is a pre-existing crash
        // filed as TS-P1-29 rather than fixed here. Dict equality keeps whatever the
        // ordinary paths already did.
        if (!IsStructurallyComparableMapping(actual) || !IsStructurallyComparableMapping(expected))
        {
            return false;
        }

        if (!ShellRecordUtilities.TryGetFields(actual, out var actualFields) ||
            !ShellRecordUtilities.TryGetFields(expected, out var expectedFields))
        {
            return false;
        }

        if (actualFields.Count != expectedFields.Count)
        {
            result = false;
            return true;
        }

        var expectedByName = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in expectedFields)
        {
            expectedByName[field.Key] = field.Value;
        }

        foreach (var field in actualFields)
        {
            if (!expectedByName.TryGetValue(field.Key, out var counterpart) ||
                !AreEqual(field.Value, counterpart))
            {
                result = false;
                return true;
            }
        }

        result = true;
        return true;
    }

    /// <summary>
    /// An *anonymous* record: a string-keyed dictionary, which is what the
    /// <c>ExpandoObject</c> behind <c>{| … |}</c> is.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes <see cref="IShellRecordObject"/>. That interface is also
    /// implemented by <c>ToshClassInstance</c>, so including it made *class instances*
    /// structurally equal and broke left-biased equality dispatch — a user's own
    /// <c>Equals</c> stopped being consulted. `TS-P1-10` is about anonymous records;
    /// a declared class decides its own identity, which is the whole point of letting it
    /// define <c>Equals</c>.
    /// </remarks>
    private static bool IsStructurallyComparableMapping(object? value) =>
        value is IDictionary<string, object?>
              or IReadOnlyDictionary<string, object?>
              // Object-keyed dictionaries too, which is what a `{% … %}` literal produces.
              // They were excluded when TS-P1-10 landed because TryGetFields threw on them —
              // the crash that became TS-P1-29 — leaving `{| a = 1, b = 2 |}` order-independent
              // while the dictionary spelling of the same mapping was order-*sensitive*. Two
              // spellings of one unordered mapping disagreeing about whether order counts
              // (TS-P1-39).
              //
              // A ToshClassInstance is deliberately still excluded: it implements
              // IShellRecordObject but not IDictionary, so the left-biased dispatch that lets a
              // declared class define its own Equals is untouched (TS-P1-26).
              or IDictionary;

    private static bool Contains(object? actual, object? expected)
    {
        if (actual is string text)
        {
            return text.Contains(ToOperatorString(expected), StringComparison.Ordinal);
        }

        if (actual is IDictionary ||
            actual is IShellEnumerableObject { HasShellItems: true } ||
            actual is IEnumerable)
        {
            return IsIn(expected, actual);
        }

        return false;
    }

    private static bool RegexMatch(object? actual, object? expected)
    {
        var input = ToOperatorString(actual);

        if (expected is Regex regex)
        {
            return regex.IsMatch(input);
        }

        var pattern = ToOperatorString(expected);
        return Regex.IsMatch(input, pattern);
    }

    private static bool IsIn(object? value, object? candidates)
    {
        if (candidates is null)
        {
            return false;
        }

        if (candidates is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (AreEqual(value, entry.Key))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is IShellEnumerableObject { HasShellItems: true } shellEnumerable)
        {
            foreach (var candidate in shellEnumerable.EnumerateShellItems())
            {
                if (AreEqual(value, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is IEnumerable enumerable && candidates is not string)
        {
            foreach (var candidate in enumerable)
            {
                if (AreEqual(value, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is string text)
        {
            return text.Contains(ToOperatorString(value), StringComparison.Ordinal);
        }

        return AreEqual(value, candidates);
    }

    private static bool StartsWith(object? actual, object? expected)
    {
        return actual is not null &&
               ToOperatorString(actual).StartsWith(
                   ToOperatorString(expected),
                   StringComparison.Ordinal);
    }

    private static bool EndsWith(object? actual, object? expected)
    {
        return actual is not null &&
               ToOperatorString(actual).EndsWith(
                   ToOperatorString(expected),
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// A bitwise operation over integers or enum members (`TS-P3-14`).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enum members unwrap to their backing value, and the result is given back as
    /// a member of that enum when — and only when — the enum was declared
    /// <c>flags</c>. An enum that was not declared combinable has no member
    /// standing for the combination, so the honest answer there is the number.
    /// </para>
    /// <para>
    /// Combining two <em>different</em> enums is refused, mirroring the rule
    /// <c>ToshEnumValue.CompareTo</c> already applies to ordering: the values are
    /// unrelated even when their backing numbers are not.
    /// </para>
    /// </remarks>
    private static object? Bitwise(object? left, object? right, string name, Func<long, long, long> combine)
    {
        var (leftBits, leftEnum) = ToBits(left, name);
        var (rightBits, rightEnum) = ToBits(right, name);

        var definition = RequireSameEnum(leftEnum, rightEnum, name);
        var result = combine(leftBits, rightBits);

        return definition is { IsFlags: true }
            ? definition.FromFlags(result)
            : NarrowBits(result, left, right);
    }

    private static object? Shift(object? left, object? right, string name, Func<long, int, long> shift)
    {
        var (bits, sourceEnum) = ToBits(left, name);
        var (amountBits, amountEnum) = ToBits(right, name);

        if (amountEnum is not null)
        {
            throw new InvalidOperationException($"The right operand of '{name}' is a shift count, not an enum member.");
        }

        var result = shift(bits, (int)amountBits);

        return sourceEnum is { IsFlags: true }
            ? sourceEnum.FromFlags(result)
            : NarrowBits(result, left, right);
    }

    /// <summary>Whether every bit of <paramref name="flag"/> is set in <paramref name="value"/>.</summary>
    /// <remarks>
    /// Tests all of the flag's bits rather than any, so a composite flag answers
    /// true only when it is wholly present — the reading `has` suggests, and the
    /// one that makes a multi-bit member such as `ReadWrite` behave.
    ///
    /// A zero flag is therefore vacuously present, matching `Enum.HasFlag` and the
    /// hand-written `Bits.Has` this replaces. An earlier draft special-cased it to
    /// false, which read well in isolation and disagreed with both.
    /// </remarks>
    private static object HasFlag(object? value, object? flag)
    {
        var (valueBits, valueEnum) = ToBits(value, "has");
        var (flagBits, flagEnum) = ToBits(flag, "has");

        RequireSameEnum(valueEnum, flagEnum, "has");

        return (valueBits & flagBits) == flagBits;
    }

    /// <summary>The integer behind an operand, and the enum it came from if any.</summary>
    private static (long Bits, IShellFlagsEnum? Enum) ToBits(object? value, string name)
    {
        if (value is IShellEnumValue enumValue)
        {
            return (Convert.ToInt64(enumValue.UnderlyingValue, CultureInfo.InvariantCulture),
                    (value as IShellTypedObject)?.ShellTypeDescriptor as IShellFlagsEnum);
        }

        if (value is bool or char or string or null || value is not IConvertible)
        {
            throw new InvalidOperationException(
                $"Operator '{name}' requires whole numbers or enum members, not '{value?.GetType().Name ?? "null"}'.");
        }

        try
        {
            return (Convert.ToInt64(value, CultureInfo.InvariantCulture), null);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException($"Operator '{name}' cannot use '{value}' as a whole number.");
        }
    }

    private static IShellFlagsEnum? RequireSameEnum(IShellFlagsEnum? left, IShellFlagsEnum? right, string name)
    {
        if (left is not null && right is not null &&
            !string.Equals(left.ShellTypeName, right.ShellTypeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operator '{name}' cannot combine members of '{left.ShellTypeName}' and '{right.ShellTypeName}'.");
        }

        return left ?? right;
    }

    /// <summary>
    /// Returns the result in the narrowest type the operands were, so `int band
    /// int` stays an `int` rather than widening every flags expression to `long`.
    /// </summary>
    private static object NarrowBits(long result, object? left, object? right)
    {
        var wide = left is long or ulong || right is long or ulong;

        // Boxed explicitly: with a bare conditional the two branches unify to
        // `long`, so the narrowing is undone by type inference and every bitwise
        // result comes back Int64 however small it is.
        return wide || result > int.MaxValue || result < int.MinValue
            ? (object)result
            : (int)result;
    }

    private static object? CastAs(object? value, object? typeSpecifier)
    {
        if (value is null)
        {
            return null;
        }

        var typeName = ToOperatorString(typeSpecifier);
        if (string.IsNullOrEmpty(typeName))
        {
            throw new InvalidOperationException("The 'as' operator requires a type name on the right-hand side.");
        }

        // A leading backtick makes the right operand a unit target rather than a
        // type name: 2`mi as `ft. This keeps the existing `as Length` contract
        // intact while giving unit conversion an idiomatic operator spelling.
        if (typeName[0] == '`')
        {
            var targetUnit = typeName[1..];
            if (string.IsNullOrWhiteSpace(targetUnit))
            {
                throw new InvalidOperationException("The 'as `unit' form requires a unit after the backtick.");
            }

            if (value is not Quantity quantity)
            {
                throw new InvalidOperationException(
                    $"Unit conversion requires a Quantity, not '{value.GetType().Name}'.");
            }

            return quantity.To(targetUnit);
        }

        // If the value is a Tosh type that's already compatible, return as-is.
        if (value is IShellTypeCheckable checkable && checkable.IsInstanceOf(typeName))
        {
            return value;
        }

        // Resolve via built-in alias table first
        Type? targetType = null;
        if (DotNetTypeResolver.BuiltInAliases.TryGetValue(typeName, out var aliased))
        {
            targetType = aliased;
        }
        else
        {
            targetType = typeName.ToLowerInvariant() switch
            {
                "str" => typeof(string),
                "boolean" => typeof(bool),
                "single" or "float32" => typeof(float),
                "float64" => typeof(double),
                "int8" => typeof(sbyte),
                "uint8" => typeof(byte),
                "int16" => typeof(short),
                "uint16" => typeof(ushort),
                "int32" => typeof(int),
                "uint32" => typeof(uint),
                "int64" => typeof(long),
                "uint64" => typeof(ulong),
                _ => Type.GetType(typeName, throwOnError: false),
            };
        }

        if (targetType is null)
        {
            throw new InvalidOperationException($"Unknown type '{typeName}' in 'as' expression.");
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (TypeConversion.TryConvert(value, targetType, out var converted))
        {
            return converted;
        }

        // `TS-P2-111`. Losing a fraction and having unrelated types are different
        // failures wanting different fixes, and the old message conflated them:
        // `7.9 as int` said "Cannot convert 'Double' to 'int'", which reads as
        // "this type never converts" even though `7.0 as int` is 7.
        if (TypeConversion.WouldTruncate(value, targetType))
        {
            throw new InvalidOperationException(
                $"Converting {value} to '{typeName}' would discard its fractional part. " +
                "Round first with Math.Round, Math.Floor, Math.Ceiling or Math.Truncate.");
        }

        throw new InvalidOperationException(
            $"Cannot convert '{value?.GetType().Name}' to '{typeName}'.");
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an instance of the named shell type, by the same walk
    /// the <c>is</c> operator uses.
    /// </summary>
    /// <remarks>
    /// Exposed so <c>cast</c> can decide "already the right type" without a second opinion:
    /// `TS-P1-24`'s lesson is that the second opinion is the one that drifts.
    /// </remarks>
    public static bool IsInstanceOfShellType(object? value, string typeName) => IsType(value, typeName);

    private static bool IsType(object? value, object? typeSpecifier)
    {
        // Handle "is null" / "is-not null" — when the type specifier is null, check nullity.
        if (typeSpecifier is null)
        {
            return value is null;
        }

        if (value is null)
        {
            return false;
        }

        if (typeSpecifier is Type type)
        {
            return type.IsInstanceOfType(value);
        }

        var typeName = ToOperatorString(typeSpecifier);
        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        // Trait-style constraint names (Numeric, Comparable, Add, etc.) — checked first
        // so they can succeed for primitive CLR values that don't implement
        // IShellTypeCheckable. Host (Tosh.Language) registers the full registry; the
        // inline fallback below covers the most common names when unhosted.
        var actualType = value.GetType();
        if (ResolveTraitConstraint is { } resolver && resolver(typeName, actualType))
        {
            return true;
        }
        if (TryMatchInlineTrait(typeName, actualType))
        {
            return true;
        }

        // Check Tosh custom types (classes, enums) via the IShellTypeCheckable interface,
        // which walks the full type hierarchy including base classes and implemented interfaces.
        if (value is IShellTypeCheckable checkable && checkable.IsInstanceOf(typeName))
        {
            return true;
        }

        // Fall back to IShellTypedObject for simple name matching (covers types that don't
        // implement IShellTypeCheckable but still carry a shell type descriptor).
        if (value is IShellTypedObject typed &&
            string.Equals(typed.ShellTypeDescriptor.ShellTypeName, typeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check simple name match (e.g. "String", "Int32", "FileSystemEntry")
        if (string.Equals(actualType.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actualType.FullName, typeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Resolve via the built-in type alias table (int, string, uint, etc.)
        if (DotNetTypeResolver.BuiltInAliases.TryGetValue(typeName, out var aliasedType))
        {
            return aliasedType.IsInstanceOfType(value);
        }

        // Additional shorthand aliases for convenience (CLR names, common alternatives)
        var resolved = typeName.ToLowerInvariant() switch
        {
            "str" => typeof(string),
            "boolean" => typeof(bool),
            "single" or "float32" => typeof(float),
            "float64" => typeof(double),
            "int8" => typeof(sbyte),
            "uint8" => typeof(byte),
            "int16" => typeof(short),
            "uint16" => typeof(ushort),
            "int32" => typeof(int),
            "uint32" => typeof(uint),
            "int64" => typeof(long),
            "uint64" => typeof(ulong),
            "datetimeoffset" => typeof(DateTimeOffset),
            _ => Type.GetType(typeName, throwOnError: false),
        };

        return resolved is not null && resolved.IsInstanceOfType(value);
    }

    private static object? Add(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '+' operator requires non-null operands.");
        }

        // Vector arithmetic
        if (left is ToshVector lv && right is ToshVector rv) return lv + rv;
        if (left is ToshVector lvs && IsNumeric(right)) return lvs + new ToshVector(Enumerable.Repeat(ToDouble(right), lvs.Length).ToArray());
        if (IsNumeric(left) && right is ToshVector rvs) return new ToshVector(Enumerable.Repeat(ToDouble(left), rvs.Length).ToArray()) + rvs;

        // Matrix arithmetic
        if (left is ToshMatrix lm && right is ToshMatrix rm) return lm + rm;
        if (left is ToshMatrix lms && IsNumeric(right)) return lms + ToshMatrix.Fill(lms.RowCount, lms.ColumnCount, ToDouble(right));
        if (IsNumeric(left) && right is ToshMatrix rms) return ToshMatrix.Fill(rms.RowCount, rms.ColumnCount, ToDouble(left)) + rms;

        // Complex arithmetic (bridge promotion only when at least one operand is already Complex)
        if ((left is Complex || right is Complex) &&
            TryPromoteToComplex(left, out var leftComplex) &&
            TryPromoteToComplex(right, out var rightComplex))
        {
            return leftComplex + rightComplex;
        }

        // Quantity arithmetic (bridge promotion only when at least one operand is already a Quantity)
        if (left is Quantity || right is Quantity)
        {
            if (TryPromoteToQuantity(left, out var leftQ) && TryPromoteToQuantity(right, out var rightQ))
            {
                return leftQ + rightQ;
            }

            throw new InvalidOperationException(
                "Quantity addition requires a compatible quantity or a losslessly representable shell bridge.");
        }

        if (left is string leftText)
        {
            return leftText + ToOperatorString(right);
        }

        if (left is DateTimeOffset dateTimeOffset && TypeConversion.TryConvert(right, typeof(TimeSpan), out var offsetSpan))
        {
            return dateTimeOffset.Add((TimeSpan)offsetSpan!);
        }

        if (left is DateTimeOffset offsetInstant && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var offsetAmount))
        {
            return ((TemporalAmount)offsetAmount!).AddTo(offsetInstant);
        }

        if (left is DateTime dateTime && TypeConversion.TryConvert(right, typeof(TimeSpan), out var dateSpan))
        {
            return dateTime.Add((TimeSpan)dateSpan!);
        }

        if (left is DateTime dateInstant && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var dateAmount))
        {
            return ((TemporalAmount)dateAmount!).AddTo(dateInstant);
        }

        if (left is TimeSpan leftSpan && TypeConversion.TryConvert(right, typeof(TimeSpan), out var rightSpan))
        {
            return leftSpan.Add((TimeSpan)rightSpan!);
        }

        if (left is TimeSpan leftTimeSpan && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var rightTemporalAmount))
        {
            return TemporalAmount.FromTimeSpan(leftTimeSpan).Add((TemporalAmount)rightTemporalAmount!);
        }

        if (left is TemporalAmount leftAmount && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var otherAmount))
        {
            return leftAmount.Add((TemporalAmount)otherAmount!);
        }

        if (left is StorageSize leftSize && TypeConversion.TryConvert(right, typeof(StorageSize), out var rightSize))
        {
            return StorageSize.FromBytes(checked(leftSize.Bytes + ((StorageSize)rightSize!).Bytes));
        }

        if (left is IEnumerable leftEnumerable and not string && right is IEnumerable rightEnumerable and not string)
        {
            var result = new List<object?>();

            foreach (var item in leftEnumerable)
            {
                result.Add(item);
            }

            foreach (var item in rightEnumerable)
            {
                result.Add(item);
            }

            return result.ToArray();
        }

        if (right is string rightText)
        {
            return ToOperatorString(left) + rightText;
        }

        return EvaluateNumeric(left, right, (lhs, rhs) => lhs + rhs, (lhs, rhs) => lhs + rhs, (lhs, rhs) => lhs + rhs);
    }

    private static object? Subtract(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '-' operator requires non-null operands.");
        }

        // Vector arithmetic
        if (left is ToshVector lv && right is ToshVector rv) return lv - rv;
        if (left is ToshVector lvs && IsNumeric(right)) return lvs - new ToshVector(Enumerable.Repeat(ToDouble(right), lvs.Length).ToArray());
        if (IsNumeric(left) && right is ToshVector rvs) return new ToshVector(Enumerable.Repeat(ToDouble(left), rvs.Length).ToArray()) - rvs;

        // Matrix arithmetic
        if (left is ToshMatrix lm && right is ToshMatrix rm) return lm - rm;
        if (left is ToshMatrix lms && IsNumeric(right)) return lms - ToshMatrix.Fill(lms.RowCount, lms.ColumnCount, ToDouble(right));
        if (IsNumeric(left) && right is ToshMatrix rms) return ToshMatrix.Fill(rms.RowCount, rms.ColumnCount, ToDouble(left)) - rms;

        // Complex arithmetic (bridge promotion only when at least one operand is already Complex)
        if ((left is Complex || right is Complex) &&
            TryPromoteToComplex(left, out var leftComplex) &&
            TryPromoteToComplex(right, out var rightComplex))
        {
            return leftComplex - rightComplex;
        }

        // Quantity arithmetic (bridge promotion only when at least one operand is already a Quantity)
        if (left is Quantity || right is Quantity)
        {
            if (TryPromoteToQuantity(left, out var leftQ) && TryPromoteToQuantity(right, out var rightQ))
            {
                return leftQ - rightQ;
            }

            throw new InvalidOperationException(
                "Quantity subtraction requires a compatible quantity or a losslessly representable shell bridge.");
        }

        if (left is DateTimeOffset leftOffset)
        {
            if (TypeConversion.TryConvert(right, typeof(TimeSpan), out var offsetSpan))
            {
                return leftOffset.Subtract((TimeSpan)offsetSpan!);
            }

            if (TypeConversion.TryConvert(right, typeof(TemporalAmount), out var offsetAmount))
            {
                return ((TemporalAmount)offsetAmount!).SubtractFrom(leftOffset);
            }

            if (TypeConversion.TryConvert(right, typeof(DateTimeOffset), out var otherOffset))
            {
                return leftOffset.Subtract((DateTimeOffset)otherOffset!);
            }
        }

        if (left is DateTime leftDateTime)
        {
            if (TypeConversion.TryConvert(right, typeof(TimeSpan), out var dateSpan))
            {
                return leftDateTime.Subtract((TimeSpan)dateSpan!);
            }

            if (TypeConversion.TryConvert(right, typeof(TemporalAmount), out var dateAmount))
            {
                return ((TemporalAmount)dateAmount!).SubtractFrom(leftDateTime);
            }

            if (TypeConversion.TryConvert(right, typeof(DateTime), out var otherDate))
            {
                return leftDateTime.Subtract((DateTime)otherDate!);
            }
        }

        if (left is TimeSpan leftSpan && TypeConversion.TryConvert(right, typeof(TimeSpan), out var rightSpan))
        {
            return leftSpan.Subtract((TimeSpan)rightSpan!);
        }

        if (left is TimeSpan spanLeft && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var temporalRight))
        {
            return TemporalAmount.FromTimeSpan(spanLeft).Subtract((TemporalAmount)temporalRight!);
        }

        if (left is TemporalAmount temporalLeft && TypeConversion.TryConvert(right, typeof(TemporalAmount), out var temporalOther))
        {
            return temporalLeft.Subtract((TemporalAmount)temporalOther!);
        }

        if (left is StorageSize leftSize && TypeConversion.TryConvert(right, typeof(StorageSize), out var rightSize))
        {
            return StorageSize.FromBytes(checked(leftSize.Bytes - ((StorageSize)rightSize!).Bytes));
        }

        return EvaluateNumeric(left, right, (lhs, rhs) => lhs - rhs, (lhs, rhs) => lhs - rhs, (lhs, rhs) => lhs - rhs);
    }

    private static object EvaluateNumeric(
        object left,
        object right,
        Func<BigInteger, BigInteger, BigInteger> integral,
        Func<double, double, double> floating,
        Func<decimal, decimal, decimal> precise)
    {
        if (IsDecimal(left) || IsDecimal(right))
        {
            return precise(ToDecimal(left), ToDecimal(right));
        }

        if (IsFloating(left) || IsFloating(right))
        {
            return floating(ToDouble(left), ToDouble(right));
        }

        if (IsIntegral(left) && IsIntegral(right))
        {
            try
            {
                var result = integral(ToBigInteger(left), ToBigInteger(right));
                return NarrowIntegralResult(result, left.GetType(), right.GetType());
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("Arithmetic overflow.");
            }
        }

        throw new InvalidOperationException(
            $"Operator operands '{DescribeOperandType(left)}' and '{DescribeOperandType(right)}' are not compatible.");
    }

    // Return the result in the wider of the two integral types, matching C# promotion rules.
    // Small types (byte, sbyte, short, ushort) promote to int, matching C# integer promotion.
    private static object NarrowIntegralResult(BigInteger result, Type leftType, Type rightType)
    {
        var resultType = WiderIntegralType(leftType, rightType);

        try
        {
            if (resultType == typeof(int)) return checked((int)result);
            if (resultType == typeof(uint)) return checked((uint)result);
            if (resultType == typeof(long)) return checked((long)result);
            if (resultType == typeof(ulong)) return checked((ulong)result);
            if (resultType == typeof(BigInteger)) return result;
        }
        catch (OverflowException)
        {
            return result;
        }

        return result;
    }

    private static Type WiderIntegralType(Type a, Type b)
    {
        // Rank: byte/sbyte/short/ushort → int, uint, long, ulong, BigInteger
        return IntegralRank(a) >= IntegralRank(b) ? CanonicalIntegralType(a) : CanonicalIntegralType(b);
    }

    private static int IntegralRank(Type t)
    {
        if (t == typeof(BigInteger)) return 5;
        if (t == typeof(ulong)) return 4;
        if (t == typeof(long)) return 3;
        if (t == typeof(uint)) return 2;
        // int, byte, sbyte, short, ushort all promote to int
        return 1;
    }

    private static Type CanonicalIntegralType(Type t)
    {
        if (t == typeof(BigInteger)) return typeof(BigInteger);
        if (t == typeof(ulong)) return typeof(ulong);
        if (t == typeof(long)) return typeof(long);
        if (t == typeof(uint)) return typeof(uint);
        return typeof(int);
    }

    private static object? Multiply(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '*' operator requires non-null operands.");
        }

        // Matrix * Matrix (matrix multiplication), Matrix * Vector, or Matrix * scalar
        if (left is ToshMatrix lm && right is ToshMatrix rm) return ToshMatrix.Multiply(lm, rm);
        if (left is ToshMatrix lmv && right is ToshVector rvv) return ToshMatrix.Multiply(lmv, rvv);
        if (left is ToshVector lvv && right is ToshMatrix rmv) return ToshMatrix.Multiply(lvv, rmv);
        if (left is ToshMatrix lms && IsNumeric(right)) return lms * ToDouble(right);
        if (IsNumeric(left) && right is ToshMatrix rms) return ToDouble(left) * rms;

        // Complex arithmetic (bridge promotion only when at least one operand is already Complex)
        if ((left is Complex || right is Complex) &&
            TryPromoteToComplex(left, out var leftComplex) &&
            TryPromoteToComplex(right, out var rightComplex))
        {
            return leftComplex * rightComplex;
        }

        // Vector * Vector (element-wise) or Vector * scalar
        if (left is ToshVector lv && right is ToshVector rv) return lv * rv;
        if (left is ToshVector lvs && IsNumeric(right)) return lvs * ToDouble(right);
        if (IsNumeric(left) && right is ToshVector rvs) return ToDouble(left) * rvs;

        // Quantity * Quantity or Quantity * scalar
        if (left is Quantity lq && right is Quantity rq)
        {
            var product = lq * rq;
            return product.Dimension.IsDimensionless ? product.BaseValue : product;
        }
        if (left is Quantity lqs && IsNumeric(right)) return lqs * ToDouble(right);
        if (IsNumeric(left) && right is Quantity rqs) return ToDouble(left) * rqs;

        // String repetition: "ha" * 3 => "hahaha", 3 * "ha" => "hahaha"
        if (left is string str && TryConvertToInt(right, out var count))
        {
            return count <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(str, count));
        }

        if (right is string str2 && TryConvertToInt(left, out var count2))
        {
            return count2 <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(str2, count2));
        }

        return EvaluateNumeric(
            left,
            right,
            (lhs, rhs) => lhs * rhs,
            (lhs, rhs) => lhs * rhs,
            (lhs, rhs) => lhs * rhs);
    }

    private static bool TryConvertToInt(object? value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case double d when d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue: result = (int)d; return true;
            default: result = 0; return false;
        }
    }

    private static object? Divide(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '/' operator requires non-null operands.");
        }

        // Matrix / Matrix (element-wise) or Matrix / scalar
        if (left is ToshMatrix lm && right is ToshMatrix rm) return lm / rm;
        if (left is ToshMatrix lms && IsNumeric(right)) return lms / ToDouble(right);

        // Complex arithmetic (bridge promotion only when at least one operand is already Complex)
        if ((left is Complex || right is Complex) &&
            TryPromoteToComplex(left, out var leftComplex) &&
            TryPromoteToComplex(right, out var rightComplex))
        {
            if (rightComplex == Complex.Zero)
            {
                throw new InvalidOperationException("Division by zero.");
            }

            return leftComplex / rightComplex;
        }

        // Vector / Vector (element-wise) or Vector / scalar
        if (left is ToshVector lv && right is ToshVector rv) return lv / rv;
        if (left is ToshVector lvs && IsNumeric(right)) return lvs / ToDouble(right);

        // Quantity / Quantity or Quantity / scalar. A cancelled dimension is a
        // scalar in ToastScript so ordinary numeric APIs can consume ratios.
        if (left is Quantity lq && right is Quantity rq)
        {
            var quotient = lq / rq;
            return quotient.Dimension.IsDimensionless ? quotient.BaseValue : quotient;
        }
        if (left is Quantity lqs && IsNumeric(right)) return lqs / ToDouble(right);
        if (IsNumeric(left) && right is Quantity rqs)
        {
            if (rqs.BaseValue == 0) throw new InvalidOperationException("Division by zero quantity.");
            if (rqs.IsAbsoluteTemperature)
            {
                throw new InvalidOperationException(
                    "Cannot divide by an absolute temperature until temperature-difference units are explicit.");
            }

            var reciprocalDimension = rqs.Dimension.Reciprocal();
            var reciprocalMagnitude = ToDouble(left) / rqs.BaseValue;
            var reciprocalSymbol = UnitRegistry.Instance.GetCanonicalUnitSymbol(reciprocalDimension);
            return UnitRegistry.Instance.CreateTyped(reciprocalMagnitude, reciprocalDimension, reciprocalSymbol);
        }

        // One rule per numeric family, and the floating one is IEEE (TS-P1-16).
        // Integral and decimal division by zero throws, matching C#; floating division
        // yields ±Infinity, or NaN for 0.0/0.0, also matching C# and IEEE 754.
        //
        // The floating lambda used to throw, which put the interpreter at odds with the
        // *constant folder*: `10.0 / 0.0` written as literals folded to Infinity while
        // `$a / $b` holding the same doubles threw. The item was filed as "depends on the
        // zero operand's type", but the real split was folded versus evaluated — two
        // implementations of one operation, disagreeing.
        return EvaluateNumeric(
            left,
            right,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs / rhs,
            (lhs, rhs) => lhs / rhs,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs / rhs);
    }

    private static object? Modulo(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '%' operator requires non-null operands.");
        }

        // Same rule as division: floating modulo by zero is NaN, not an error (TS-P1-16).
        return EvaluateNumeric(
            left,
            right,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs % rhs,
            (lhs, rhs) => lhs % rhs,
            (lhs, rhs) => rhs == 0 ? throw new InvalidOperationException("Division by zero.") : lhs % rhs);
    }

    /// <summary>
    /// Floor (integer) division. <c>a // b</c> equals <c>floor(a / b)</c> for any
    /// numeric operands. For two integers the result is an <c>int</c>/<c>long</c>;
    /// for floats the result is a <c>double</c> rounded toward negative infinity
    /// (so <c>-7 // 2 == -4</c>, matching Python's semantics).
    /// </summary>
    private static object? FloorDivide(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '//' operator requires non-null operands.");
        }

        return EvaluateNumeric(
            left,
            right,
            (lhs, rhs) =>
            {
                if (rhs == 0) throw new InvalidOperationException("Division by zero.");
                // Truncated division rounds toward zero; floor must round toward -∞.
                var quotient = lhs / rhs;
                if ((lhs % rhs != 0) && ((lhs < 0) ^ (rhs < 0))) quotient--;
                return quotient;
            },
            (lhs, rhs) => rhs == 0.0
                ? throw new InvalidOperationException("Division by zero.")
                : Math.Floor(lhs / rhs),
            (lhs, rhs) =>
            {
                if (rhs == 0) throw new InvalidOperationException("Division by zero.");
                var quotient = lhs / rhs;
                if ((lhs % rhs != 0L) && ((lhs < 0) ^ (rhs < 0))) quotient--;
                return quotient;
            });
    }

    private static object? Power(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new InvalidOperationException("The '**' operator requires non-null operands.");
        }

        if ((left is Complex || right is Complex) &&
            TryPromoteToComplex(left, out var leftComplex) &&
            TryPromoteToComplex(right, out var rightComplex))
        {
            return Complex.Pow(leftComplex, rightComplex);
        }

        var lhs = ToDouble(left);
        var rhs = ToDouble(right);
        var result = Math.Pow(lhs, rhs);

        // Return int when the result is a whole number and both operands were integral
        if (IsIntegral(left) && IsIntegral(right) && rhs >= 0 && result == Math.Floor(result) && result is >= int.MinValue and <= int.MaxValue)
        {
            return (int)result;
        }

        return result;
    }

    public static bool ToBoolean(object? value) => ToshTruthiness.IsTruthy(value);

    private static bool IsIntegral(object value)
    {
        var type = value.GetType();
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(BigInteger);
    }

    private static bool IsFloating(object value)
    {
        var type = value.GetType();
        return type == typeof(float) || type == typeof(double);
    }

    private static bool IsDecimal(object value) => value.GetType() == typeof(decimal);

    private static BigInteger ToBigInteger(object value)
    {
        return value switch
        {
            BigInteger integer => integer,
            byte number => new BigInteger(number),
            sbyte number => new BigInteger(number),
            short number => new BigInteger(number),
            ushort number => new BigInteger(number),
            int number => new BigInteger(number),
            uint number => new BigInteger(number),
            long number => new BigInteger(number),
            ulong number => new BigInteger(number),
            _ => throw new InvalidOperationException($"Value of type '{DescribeOperandType(value)}' cannot be converted to BigInteger."),
        };
    }

    private static double ToDouble(object value) => value is BigInteger integer
        ? (double)integer
        : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static decimal ToDecimal(object value) => value is BigInteger integer
        ? (decimal)integer
        : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static bool IsNumeric(object? value) => value is not null && (IsIntegral(value) || IsFloating(value) || IsDecimal(value));

    private static bool TryPromoteToComplex(object? value, out Complex complex)
    {
        return ComplexShellType.TryConvert(value, out complex);
    }

    /// <summary>
    /// Attempts to promote a value to a Quantity. Returns true for:
    /// - Quantity (pass-through)
    /// - TimeSpan → DurationQuantity (bridge)
    /// - StorageSize → DataSizeQuantity (bridge)
    /// </summary>
    private static bool TryPromoteToQuantity(object? value, out Quantity quantity)
    {
        switch (value)
        {
            case Quantity q:
                quantity = q;
                return true;
            case TimeSpan ts:
                quantity = new DurationQuantity(ts.TotalSeconds, "s");
                return true;
            case StorageSize ss:
                if (TypeConversion.TryConvert(ss, typeof(DataSizeQuantity), out var dataSize) &&
                    dataSize is Quantity convertedDataSize)
                {
                    quantity = convertedDataSize;
                    return true;
                }
                break;
            default:
                break;
        }

        quantity = null!;
        return false;
    }

    // Numeric CLR types recognised by inline trait fallback for `is Numeric` etc.
    private static readonly HashSet<Type> _numericClrTypes = new()
    {
        typeof(byte), typeof(sbyte),
        typeof(short), typeof(ushort),
        typeof(int), typeof(uint),
        typeof(long), typeof(ulong),
        typeof(float), typeof(double),
        typeof(decimal),
        typeof(Half),
        typeof(BigInteger),
    };

    private static bool TryMatchInlineTrait(string name, Type type)
    {
        // Mirrors a subset of ToshTypeParameterConstraintRegistry so the `is`
        // operator works for the most common trait constraints even before the
        // language host has registered ResolveTraitConstraint.
        return name switch
        {
            var n when string.Equals(n, "Numeric", StringComparison.OrdinalIgnoreCase) => _numericClrTypes.Contains(type),
            var n when string.Equals(n, "Number", StringComparison.OrdinalIgnoreCase) => _numericClrTypes.Contains(type),
            var n when string.Equals(n, "INumber", StringComparison.OrdinalIgnoreCase) => _numericClrTypes.Contains(type),
            var n when string.Equals(n, "Comparable", StringComparison.OrdinalIgnoreCase) => typeof(IComparable).IsAssignableFrom(type),
            var n when string.Equals(n, "Eq", StringComparison.OrdinalIgnoreCase) => true,
            var n when string.Equals(n, "Add", StringComparison.OrdinalIgnoreCase) => HasCompatibleOperator(type, "op_Addition"),
            var n when string.Equals(n, "Sub", StringComparison.OrdinalIgnoreCase) => HasCompatibleOperator(type, "op_Subtraction"),
            var n when string.Equals(n, "Mul", StringComparison.OrdinalIgnoreCase) => HasCompatibleOperator(type, "op_Multiply"),
            var n when string.Equals(n, "Div", StringComparison.OrdinalIgnoreCase) => HasCompatibleOperator(type, "op_Division"),
            _ => false,
        };
    }

    private static bool HasCompatibleOperator(Type type, string methodName)
    {
        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Any(method =>
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) return false;
                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                    parameters[0].ParameterType.IsAssignableFrom(type) &&
                    parameters[1].ParameterType.IsAssignableFrom(type);
            });
    }
}

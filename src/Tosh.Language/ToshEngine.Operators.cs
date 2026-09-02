using System.Collections;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Operators: binary dispatch and its fallbacks, equality, comparison, regex matching
/// and pattern matching.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// **`AreEqualAsync` is here, and that is the point of this file.** The separation plan
/// carries an open item on equality convergence: `OperatorEvaluator.AreEqual` and
/// `ToshEngine.AreEqualAsync` are structurally parallel implementations sharing only
/// `TryCompareByName`, they live in different types so the name-based twin discovery
/// does not even pair them, and they have already diverged twice — `TS-P1-14` and
/// `TS-P1-15`. Collecting the engine's half into one file does not converge them, but
/// it makes the comparison a matter of reading one file against one other, which is the
/// first step that item asks for: guard before converging, because agreement today
/// should not be assumed.
/// </summary>
public sealed partial class ToshEngine
{

    /// <summary>
    /// Evaluates an eager binary operator through ToastScript's asynchronous
    /// class protocol before falling back to the shared CLR/runtime semantics.
    /// Ordinary expressions and compound assignments both use this boundary so
    /// overload order, cancellation, user throws, and structured diagnostics do
    /// not depend on the syntax that reached the operator.
    /// </summary>
    /// <summary>
    /// Applies a binary operator, taking a synchronous path when neither operand can
    /// reach an <c>await</c>.
    /// </summary>
    /// <remarks>
    /// Placed here rather than at the call sites so every caller benefits — a compound
    /// assignment (<c>$x += 1</c>), a chained comparison and an operator argument all
    /// arrive through this one method (<c>TS-P2-125</c>). It calls the same
    /// <see cref="OperatorEvaluator.EvaluateBinary"/> the async path reaches, so this
    /// is a shortcut to the existing implementation and not a second copy of it —
    /// which is the distinction <c>TS-P1-24</c> is about.
    /// </remarks>
    private ValueTask<object?> EvaluateBinaryOperatorAsync(
        string sourceName,
        string sourceText,
        TextSpan span,
        object? left,
        string @operator,
        object? right,
        CancellationToken cancellationToken)
    {
        if (IsSynchronousArithmeticOperator(@operator) &&
            IsPrimitiveNumber(left) &&
            IsPrimitiveNumber(right))
        {
            try
            {
                return new ValueTask<object?>(OperatorEvaluator.EvaluateBinary(left, @operator, right));
            }
            catch (ToshDiagnosticException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateExpressionDiagnostic(sourceName, sourceText, span, exception);
            }
        }

        return EvaluateBinaryOperatorSlowAsync(sourceName, sourceText, span, left, @operator, right, cancellationToken);
    }

    private async ValueTask<object?> EvaluateBinaryOperatorSlowAsync(
        string sourceName,
        string sourceText,
        TextSpan span,
        object? left,
        string @operator,
        object? right,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Symmetric operator overloading is intentionally left-biased:
            // consult the right operand only when the left has no matching
            // overload, preserving the established ToastScript protocol.
            if (left is ToshClassInstance leftInstance)
            {
                var leftOverload = await TryInvokeClassBinaryOperatorAsync(
                    leftInstance,
                    @operator,
                    right,
                    reversed: false,
                    cancellationToken);
                if (leftOverload.Matched)
                {
                    return leftOverload.Value;
                }
            }

            if (right is ToshClassInstance rightInstance)
            {
                // This instance is the *right* operand, so a two-argument overload is
                // told so and can order the operands correctly (`TOAST-0106`).
                var rightOverload = await TryInvokeClassBinaryOperatorAsync(
                    rightInstance,
                    @operator,
                    left,
                    reversed: true,
                    cancellationToken);
                if (rightOverload.Matched)
                {
                    return rightOverload.Value;
                }
            }

            return await EvaluateFallbackBinaryOperatorAsync(
                left,
                @operator,
                right,
                cancellationToken);
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ShellControlFlowException)
        {
            throw;
        }
        catch (Exception exception) when (IsToshThrown(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateExpressionDiagnostic(
                sourceName,
                sourceText,
                span,
                exception);
        }
    }

    private async ValueTask<object?> EvaluateFallbackBinaryOperatorAsync(
        object? left,
        string @operator,
        object? right,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (left is ShellTextLine leftLine)
        {
            left = leftLine.Text;
        }
        if (right is ShellTextLine rightLine)
        {
            right = rightLine.Text;
        }

        return @operator switch
        {
            "==" => await AreEqualAsync(left, right, cancellationToken),
            "!=" => !await AreEqualAsync(left, right, cancellationToken),
            "in" or "is-in" => await IsInAsync(left, right, cancellationToken),
            "not-in" or "is-not-in" => !await IsInAsync(left, right, cancellationToken),
            "=~" => await RegexMatchAsync(left, right, cancellationToken),
            "!~" => !await RegexMatchAsync(left, right, cancellationToken),
            "contains" => await ContainsAsync(left, right, cancellationToken),
            "starts-with" => await StartsWithAsync(left, right, cancellationToken),
            "ends-with" => await EndsWithAsync(left, right, cancellationToken),
            // `TOAST-0018`. A null operand raises here as it does for every other
            // arithmetic operator. It used to render as the empty string, so
            // `null + "a"` was `"a"` while `null + 1` raised — a missing value vanished
            // silently into concatenated output. Write `($x ?? "") + "a"` to opt in.
            "+" when (left is string || right is string) && (left is null || right is null) =>
                throw new InvalidOperationException(Tosh.Runtime.ToastMessages.NullStringConcatenation),
            "+" when left is string => (string)left
                + await ToOperatorStringAsync(right, cancellationToken),
            "+" when right is string => await ToOperatorStringAsync(left, cancellationToken)
                + (string)right,
            _ => await EvaluateClrFallbackOperatorAsync(
                left,
                @operator,
                right,
                cancellationToken),
        };
    }

    private async ValueTask<object?> EvaluateClrFallbackOperatorAsync(
        object? left,
        string @operator,
        object? right,
        CancellationToken cancellationToken)
    {
        // OperatorEvaluator performs string conversions synchronously. Keep
        // explicit CLR operators as the compatibility boundary, but never let
        // that fallback synchronously re-enter a ToastScript ToString body.
        if (@operator is not ("is" or "is-not" or "as") &&
            left is string && right is ToshClassInstance)
        {
            right = await ToOperatorStringAsync(right, cancellationToken);
        }
        else if (@operator is not ("is" or "is-not" or "as") &&
                 right is string && left is ToshClassInstance)
        {
            left = await ToOperatorStringAsync(left, cancellationToken);
        }
        else if (@operator is "is" or "is-not" or "as" &&
                 right is ToshClassInstance)
        {
            right = await ToOperatorStringAsync(right, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (@operator == "as")
        {
            return OperatorEvaluator.CastAs(left, right, ResolveAsTarget);
        }

        return OperatorEvaluator.EvaluateBinary(left, @operator, right, ResolveDeclaredTypeName);
    }

    /// <summary>
    /// Maps a module-qualified declared-type name to the simple name its instances answer to,
    /// or <c>null</c> when the name does not resolve to a declared type.
    /// </summary>
    /// <remarks>
    /// <c>TOAST-0105</c>. Threaded into <c>is</c> for the same reason <see cref="ResolveAsTarget"/>
    /// is threaded into <c>as</c>: the lookup needs local, nested, imported and module-qualified
    /// declarations, all of which live in engine scope, and the portable operator runtime holds
    /// no engine state.
    ///
    /// Only dotted names are looked up. An unqualified name already matches by simple name in
    /// the operator runtime, so asking here would cost a table walk per type test to reach the
    /// same answer.
    /// </remarks>
    private string? ResolveDeclaredTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName) || !typeName.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        return TryGetNamedType(typeName, out var definition) ? ((IShellStaticType)definition).ShellTypeName : null;
    }

    /// <summary>
    /// Resolves an <c>as</c> target by the same CLR-first, declaration-second rule as the
    /// <c>cast</c> command. Keeping the scoped lookup here lets local, nested, imported, and
    /// module-qualified declarations participate without putting engine state in the portable
    /// operator runtime.
    /// </summary>
    private object? ResolveAsTarget(string typeName)
    {
        if (ResolveTypeName(typeName) is { } clrType)
        {
            return clrType;
        }

        return TryResolveShellStaticType(typeName, out var shellType) &&
               shellType is IShellTypeDescriptor descriptor
            ? descriptor
            : null;
    }

    private async ValueTask<bool> MatchesOperatorAsync(
        object? actual,
        string @operator,
        object? expected,
        bool nullable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (actual is ShellTextLine actualLine)
        {
            actual = actualLine.Text;
        }
        if (expected is ShellTextLine expectedLine)
        {
            expected = expectedLine.Text;
        }

        switch (@operator)
        {
            case "==":
                return await AreEqualAsync(actual, expected, cancellationToken);
            case "!=":
                return !await AreEqualAsync(actual, expected, cancellationToken);
            case "in":
                return await IsInAsync(actual, expected, cancellationToken);
            case "not-in":
                return !await IsInAsync(actual, expected, cancellationToken);
            case "=~":
                return await RegexMatchAsync(actual, expected, cancellationToken);
            case "!~":
                return !await RegexMatchAsync(actual, expected, cancellationToken);
            case "contains":
                return await ContainsAsync(actual, expected, cancellationToken);
            case "starts-with":
                return await StartsWithAsync(actual, expected, cancellationToken);
            case "ends-with":
                return await EndsWithAsync(actual, expected, cancellationToken);
        }

        if (@operator is not ("is" or "is-not") &&
            actual is string && expected is ToshClassInstance)
        {
            expected = await ToOperatorStringAsync(expected, cancellationToken);
        }
        else if (@operator is not ("is" or "is-not") &&
                 expected is string && actual is ToshClassInstance)
        {
            actual = await ToOperatorStringAsync(actual, cancellationToken);
        }
        else if (@operator is "is" or "is-not" &&
                 expected is ToshClassInstance)
        {
            expected = await ToOperatorStringAsync(expected, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return OperatorEvaluator.Matches(actual, @operator, expected, nullable, ResolveDeclaredTypeName);
    }

    /// <summary>
    /// Asynchronous equality, kept alongside <see cref="OperatorEvaluator.AreEqual"/>
    /// because a user-defined <c>Equals</c> may be asynchronous.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> so <c>EqualityParityTests</c> can
    /// compare the two implementations directly instead of inferring which path a
    /// script reached — the same reason <c>TS-P1-24</c> opened
    /// <c>TryConvertAnnotatedValue</c> to its drift guard. These two already
    /// diverged once: the <c>TS-P1-14</c> equality change landed here only after
    /// <c>TS-P1-15</c> discovered it had been applied to the evaluator alone.
    /// </remarks>
    internal async ValueTask<bool> AreEqualAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Records and dictionaries compare by name, not by enumeration order
        // (TS-P1-10). Delegated to OperatorEvaluator rather than reimplemented: this
        // method and OperatorEvaluator.AreEqual are the parallel pair TS-P1-24 was filed
        // for, and the first attempt at this fix landed only on the synchronous side —
        // `==` goes through here, so the defect survived a change that looked complete.
        if (OperatorEvaluator.TryCompareByName(actual, expected, out var byName))
        {
            return byName;
        }

        // Preserve OperatorEvaluator's collection-first semantics, including
        // recursive element comparison and ordered enumeration.
        if (actual is IEnumerable actualCollection && actual is not string &&
            expected is IEnumerable expectedCollection && expected is not string)
        {
            var actualEnumerator = actualCollection.GetEnumerator();
            var expectedEnumerator = expectedCollection.GetEnumerator();

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

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

                    if (!await AreEqualAsync(
                            actualEnumerator.Current,
                            expectedEnumerator.Current,
                            cancellationToken))
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
            return actual is null && expected is null;
        }

        // TS-P1-15: an enum member equals another member of the same
        // enum and equals its own backing value. Kept in step with
        // OperatorEvaluator.AreEqual so both surfaces agree.
        if ((actual is IShellEnumValue || expected is IShellEnumValue) &&
            actual is not string && expected is not string &&
            ShellEnumComparison.AreEquivalent(actual, expected))
        {
            return true;
        }

        // `TOAST-0018`. Delegated, not reimplemented — the same reason `TryCompareByName`
        // above is. The first attempt at *this* fix landed only on `OperatorEvaluator`
        // and changed nothing observable, because `==` comes through here: exactly the
        // failure this file's header records for `TS-P1-14`, repeated.
        if (actual is not null && expected is not null &&
            OperatorEvaluator.TryCompareIntegerWithFloat(actual, expected, out var exactNumeric))
        {
            return exactNumeric;
        }

        // `TOAST-0026`. Delegated for the same reason, and missed for the same reason: the
        // rule was added to `OperatorEvaluator` alone and `==` still answered the old way,
        // which is the third time this file's header has been proved right in one session.
        if (actual is not null && expected is not null &&
            OperatorEvaluator.TryCompareDecimalWithFloat(actual, expected, out var exactDecimal))
        {
            return exactDecimal;
        }

        // TS-P1-26: both directions are attempted, and equality holds if either
        // matches. See OperatorEvaluator.AreEqual for why returning on the first
        // successful *conversion* rather than the first successful *equality*
        // made this asymmetric.
        var convertedExpected = await TryConvertForEqualityAsync(
            expected,
            actual.GetType(),
            cancellationToken);
        if (convertedExpected.Converted &&
            await ObjectEqualsAsync(actual, convertedExpected.Value, cancellationToken))
        {
            return true;
        }

        var convertedActual = await TryConvertForEqualityAsync(
            actual,
            expected.GetType(),
            cancellationToken);
        if (convertedActual.Converted &&
            await ObjectEqualsAsync(convertedActual.Value, expected, cancellationToken))
        {
            return true;
        }

        // A successful conversion decides the answer even when it produced
        // unequal values; see OperatorEvaluator.AreEqual. Falling through here
        // would dispatch a user-defined `Equals` with an operand of the wrong
        // type — `ValueProbe.Equals("PROBE")` reading `$other.Value` off a
        // string — which the original early return shielded against.
        if (convertedExpected.Converted || convertedActual.Converted)
        {
            return false;
        }

        // TS-P1-14: no case-insensitive ToString fallback here either.
        // This mirrors OperatorEvaluator.AreEqual so the interpreter and
        // the shared runtime dispatcher agree on what equality means.
        return await ObjectEqualsAsync(actual, expected, cancellationToken);
    }

    private async ValueTask<(bool Converted, object? Value)> TryConvertForEqualityAsync(
        object? value,
        Type targetType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveType == typeof(string) && value is ToshClassInstance)
        {
            return (
                true,
                await ToOperatorStringAsync(value, cancellationToken));
        }

        return TypeConversion.TryConvert(value, targetType, out var converted)
            ? (true, converted)
            : (false, null);
    }

    private async ValueTask<bool> ObjectEqualsAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ReferenceEquals(actual, expected))
        {
            return true;
        }
        if (actual is null || expected is null)
        {
            return false;
        }

        if (actual is ToshClassInstance classInstance)
        {
            var invocation = await classInstance.Definition.TryInvokeSpecialInstanceMethodAsync(
                classInstance,
                nameof(Equals),
                new object?[] { expected },
                cancellationToken);
            return invocation.Matched && OperatorEvaluator.ToBoolean(invocation.Value);
        }

        if (actual is ToshRecordInstance record)
        {
            if (expected is not ToshRecordInstance otherRecord ||
                !ReferenceEquals(record.Definition, otherRecord.Definition))
            {
                return false;
            }

            foreach (var field in record.Definition.Fields)
            {
                record.TryGetMember(field.Name, out var leftValue);
                otherRecord.TryGetMember(field.Name, out var rightValue);
                if (!await AreEqualAsync(leftValue, rightValue, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is ToshStructInstance @struct)
        {
            if (expected is not ToshStructInstance otherStruct ||
                !ReferenceEquals(@struct.Definition, otherStruct.Definition))
            {
                return false;
            }

            foreach (var field in @struct.Definition.Fields)
            {
                @struct.TryGetMember(field.Name, out var leftValue);
                otherStruct.TryGetMember(field.Name, out var rightValue);
                if (!await AreEqualAsync(leftValue, rightValue, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is ToshUnionVariantInstance union)
        {
            if (expected is not ToshUnionVariantInstance otherUnion ||
                !string.Equals(
                    union.UnionDefinition.Name,
                    otherUnion.UnionDefinition.Name,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    union.VariantName,
                    otherUnion.VariantName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fields = union.GetMembers()
                .Where(member => !string.Equals(
                    member.Key,
                    "Variant",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var otherFields = otherUnion.GetMembers()
                .Where(member => !string.Equals(
                    member.Key,
                    "Variant",
                    StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    member => member.Key,
                    member => member.Value,
                    StringComparer.OrdinalIgnoreCase);
            if (fields.Length != otherFields.Count)
            {
                return false;
            }

            foreach (var field in fields)
            {
                if (!otherFields.TryGetValue(field.Key, out var rightValue) ||
                    !await ObjectEqualsAsync(field.Value, rightValue, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is DictionaryEntry entry)
        {
            return expected is DictionaryEntry otherEntry
                && await ObjectEqualsAsync(entry.Key, otherEntry.Key, cancellationToken)
                && await ObjectEqualsAsync(entry.Value, otherEntry.Value, cancellationToken);
        }

        var actualType = actual.GetType();
        if (actualType.IsGenericType &&
            actualType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            if (expected.GetType() != actualType)
            {
                return false;
            }

            var keyProperty = actualType.GetProperty(nameof(KeyValuePair<object, object>.Key))!;
            var valueProperty = actualType.GetProperty(nameof(KeyValuePair<object, object>.Value))!;
            return await ObjectEqualsAsync(
                    keyProperty.GetValue(actual),
                    keyProperty.GetValue(expected),
                    cancellationToken)
                && await ObjectEqualsAsync(
                    valueProperty.GetValue(actual),
                    valueProperty.GetValue(expected),
                    cancellationToken);
        }

        // Arbitrary CLR Equals implementations are intentionally synchronous;
        // they are a host protocol boundary rather than ToastScript execution.
        return actual.Equals(expected);
    }

    private async ValueTask<bool> RegexMatchAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        var input = await ToOperatorStringAsync(actual, cancellationToken);
        if (expected is Regex regex)
        {
            return regex.IsMatch(input);
        }

        var pattern = await ToOperatorStringAsync(expected, cancellationToken);
        return Regex.IsMatch(input, pattern);
    }

    private async ValueTask<string> ToOperatorStringAsync(
        object? value,
        CancellationToken cancellationToken) =>
        await ToOperatorStringAsync(
            value,
            cancellationToken,
            new HashSet<ToshClassInstance>(ReferenceEqualityComparer.Instance));

    private async ValueTask<string> ToOperatorStringAsync(
        object? value,
        CancellationToken cancellationToken,
        HashSet<ToshClassInstance> activeInstances)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (value is not ToshClassInstance instance)
        {
            return value?.ToString() ?? string.Empty;
        }

        if (!activeInstances.Add(instance))
        {
            return instance.Definition.Name;
        }

        try
        {
            var invocation = await instance.Definition.TryInvokeSpecialInstanceMethodAsync(
                instance,
                nameof(ToString),
                Array.Empty<object?>(),
                cancellationToken);
            if (!invocation.Matched)
            {
                return instance.Definition.Name;
            }

            return invocation.Value is ToshClassInstance nested
                ? await ToOperatorStringAsync(
                    nested,
                    cancellationToken,
                    activeInstances)
                : invocation.Value?.ToString() ?? string.Empty;
        }
        finally
        {
            activeInstances.Remove(instance);
        }
    }

    /// <summary>
    /// Whether a value is this variant, with every sub-pattern satisfied — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Structural rather than a comparison. The variant must be *this* one; a positional
    /// pattern must not ask for more fields than it has; and each sub-pattern must itself
    /// match, which is where nesting happens — a sub-pattern may be another variant pattern.
    /// </para>
    /// <para>
    /// A bareword sub-pattern is a *binding*, so it matches anything: `Ok(v)` accepts whatever
    /// `value` holds. Only a written value — a literal, a string — is compared, which is what
    /// makes `Node { kind: "if", body }` test one field and bind the other.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether a variant pattern's name refers to this subject — <c>TOAST-0095</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pattern may name a variant bare (<c>Ok(v)</c>) or qualified by the union that declares
    /// it (<c>Result.Ok(v)</c>, or <c>Result::Ok(v)</c>, which canonicalises to the same text).
    /// The comparison used to be whole-string equality against the variant name, so every
    /// qualified pattern failed it and the arm fell through to <c>default</c> — no diagnostic,
    /// no match, and an arm that reads as live. That is worse in <c>match</c> than anywhere
    /// else, because the construct exists to be exhaustive, and <c>TOAST-0054</c> would judge
    /// exhaustiveness on arms that can never fire.
    /// </para>
    /// <para>
    /// A qualifier naming the <i>wrong</i> union is a mistake in the pattern rather than a value
    /// that failed to match, so it is reported for the same reason the arity checks below are:
    /// silence would spell it as a dead arm.
    /// </para>
    /// </remarks>
    private bool PatternNamesSubject(
        VariantPatternSyntax pattern,
        PatternSubject subject,
        string sourceName,
        string sourceText)
    {
        var name = pattern.VariantName;

        var separator = name.LastIndexOf('.');
        if (separator < 0)
        {
            return string.Equals(subject.TypeName, name, StringComparison.Ordinal);
        }

        var qualifier = name[..separator];
        var member = name[(separator + 1)..];

        if (!string.Equals(subject.TypeName, member, StringComparison.Ordinal))
        {
            return false;
        }

        // The variant matches; only the qualifier is left to agree. A subject that declares no
        // owner cannot confirm one, so the qualified spelling is simply not for it.
        if (subject.OwnerName is not { } owner)
        {
            return false;
        }

        if (string.Equals(owner, qualifier, StringComparison.Ordinal))
        {
            return true;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.pattern_wrong_qualifier",
            Title: $"'{member}' is declared by '{owner}', not by '{qualifier}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: pattern.Span,
            Label: $"'{qualifier}' does not declare '{member}'",
            Help: $"write `{owner}.{member}` or the bare `{member}`."));
    }

    private async Task<bool> MatchesVariantPatternAsync(
        object? value,
        string sourceName,
        string sourceText,
        VariantPatternSyntax pattern,
        CancellationToken cancellationToken)
    {
        if (!TryDescribePatternSubject(value, out var subject))
        {
            return false;
        }

        if (!PatternNamesSubject(pattern, subject, sourceName, sourceText))
        {
            return false;
        }

        var label = subject.Named.Count == 0
            ? $"'{subject.TypeName}' has no fields"
            : $"'{subject.TypeName}' declares: {string.Join(", ", subject.Named)}";

        // `TOAST-0053`. Past this point the value *is* what the pattern names, so a pattern
        // that does not fit it is a mistake in the pattern rather than a value that failed to
        // match. Saying so beats falling through to `default`, which is what a silent `false`
        // would do — the arm would simply never run and nothing would say why.
        if (pattern.Positional.Count > 0 && subject.Positional.Count == 0 && subject.Named.Count > 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.pattern_arity",
                Title: $"A {subject.Kind} cannot be destructured positionally.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: pattern.Span,
                Label: label,
                Help: $"name the fields — `{pattern.VariantName} {{ {subject.Named[0]} }}`. "
                    + $"A {subject.Kind}'s members may be inherited, reordered or added, so "
                    + "there is no position to bind against."));
        }

        if (pattern.Positional.Count > subject.Positional.Count)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.pattern_arity",
                Title: $"Pattern for '{pattern.VariantName}' binds {pattern.Positional.Count} "
                     + $"field(s), but '{pattern.VariantName}' declares {subject.Positional.Count}.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: pattern.Span,
                Label: label,
                Help: "bind fewer fields, or name them with `{ field }` to pick the ones you want."));
        }

        foreach (var named in pattern.Named)
        {
            if (subject.Named.Contains(named.Field, StringComparer.Ordinal)) { continue; }

            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.pattern_unknown_field",
                Title: $"'{pattern.VariantName}' has no field '{named.Field}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: named.Span,
                Label: label,
                Help: NearestFieldHelp(named.Field, subject.Named)));
        }

        for (var index = 0; index < pattern.Positional.Count; index++)
        {
            TryReadPatternMember(value, subject.Positional[index], out var fieldValue);
            if (!await SubPatternMatchesAsync(
                    fieldValue, sourceName, sourceText, pattern.Positional[index], cancellationToken))
            {
                return false;
            }
        }

        foreach (var named in pattern.Named)
        {
            TryReadPatternMember(value, named.Field, out var fieldValue);
            if (!await SubPatternMatchesAsync(
                    fieldValue, sourceName, sourceText, named.Pattern, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Suggests the closest declared field, when one is close enough to mean it.</summary>
    private static string NearestFieldHelp(string written, IReadOnlyList<string> fields)
    {
        foreach (var field in fields)
        {
            if (field.StartsWith(written, StringComparison.OrdinalIgnoreCase) ||
                written.StartsWith(field, StringComparison.OrdinalIgnoreCase))
            {
                return $"did you mean '{field}'?";
            }
        }

        return "name a field the variant declares, or bind positionally with `(…)`.";
    }

    /// <summary>
    /// Whether a sequence has the shape a list pattern describes — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Length first, then elements. Without a rest the length must be exact, so <c>[a, b]</c>
    /// does not match a three-element list; with one, the fixed sub-patterns on either side
    /// have to fit and the middle absorbs whatever is left, which may be nothing.
    /// </para>
    /// <para>
    /// A string is a sequence of characters to .NET and is not one here. Matching
    /// <c>[a, b]</c> against "hi" would bind two characters, which is not what anybody writing
    /// a list pattern means, and would make a string silently match a pattern meant for a list.
    /// </para>
    /// </remarks>
    private async Task<bool> MatchesListPatternAsync(
        object? value,
        string sourceName,
        string sourceText,
        ListPatternSyntax pattern,
        CancellationToken cancellationToken)
    {
        if (!TryReadPatternSequence(value, out var items)) { return false; }

        var fixedCount = pattern.Before.Count + pattern.After.Count;

        if (pattern.HasRest ? items.Count < fixedCount : items.Count != fixedCount)
        {
            return false;
        }

        for (var index = 0; index < pattern.Before.Count; index++)
        {
            if (!await SubPatternMatchesAsync(
                    items[index], sourceName, sourceText, pattern.Before[index], cancellationToken))
            {
                return false;
            }
        }

        for (var index = 0; index < pattern.After.Count; index++)
        {
            var item = items[items.Count - pattern.After.Count + index];
            if (!await SubPatternMatchesAsync(
                    item, sourceName, sourceText, pattern.After[index], cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A sub-pattern: a name binds and matches anything, anything else compares.</summary>
    private async Task<bool> SubPatternMatchesAsync(
        object? value,
        string sourceName,
        string sourceText,
        ArgumentSyntax pattern,
        CancellationToken cancellationToken)
        => pattern is BarewordArgumentSyntax
            || await MatchesPatternAsync(value, sourceName, sourceText, pattern, cancellationToken);

    private async Task<bool> MatchesPatternAsync(
        object? switchValue,
        string sourceName,
        string sourceText,
        ArgumentSyntax pattern,
        CancellationToken cancellationToken)
    {
        switch (pattern)
        {
            case ComparisonPatternSyntax cp:
                {
                    var operand = await EvaluateArgumentAsync(sourceName, sourceText, cp.Operand, cancellationToken);
                    return await MatchesOperatorAsync(
                        switchValue,
                        cp.Operator,
                        operand,
                        nullable: false,
                        cancellationToken);
                }

            case RangeArgumentSyntax range:
                {
                    var startValue = await EvaluateArgumentAsync(sourceName, sourceText, range.Start, cancellationToken);
                    if (range.End is null)
                    {
                        // Infinite range in match: only check lower bound
                        return await MatchesOperatorAsync(
                            switchValue,
                            ">=",
                            startValue,
                            nullable: false,
                            cancellationToken);
                    }
                    var endValue = await EvaluateArgumentAsync(sourceName, sourceText, range.End, cancellationToken);
                    return await MatchesOperatorAsync(
                            switchValue,
                            ">=",
                            startValue,
                            nullable: false,
                            cancellationToken)
                        && await MatchesOperatorAsync(
                            switchValue,
                            "<=",
                            endValue,
                            nullable: false,
                            cancellationToken);
                }

            case VariantPatternSyntax variant:
                return await MatchesVariantPatternAsync(
                    switchValue, sourceName, sourceText, variant, cancellationToken);

            case ListPatternSyntax list:
                return await MatchesListPatternAsync(
                    switchValue, sourceName, sourceText, list, cancellationToken);

            case OrPatternSyntax alternatives:
                foreach (var alternative in alternatives.Alternatives)
                {
                    if (await MatchesPatternAsync(
                            switchValue, sourceName, sourceText, alternative, cancellationToken))
                    {
                        return true;
                    }
                }

                return false;

            case BoundPatternSyntax bound:
                // `as` binds; it never changes whether the pattern matched.
                return await MatchesPatternAsync(
                    switchValue, sourceName, sourceText, bound.Pattern, cancellationToken);

            default:
                {
                    var patternValue = await EvaluateArgumentAsync(sourceName, sourceText, pattern, cancellationToken);
                    return await AreEqualAsync(switchValue, patternValue, cancellationToken);
                }
        }
    }

    /// <summary>
    /// Operators whose evaluation never awaits. Comparison, membership and regex all
    /// do, and `+` awaits when either side is a string, so none of them are here.
    /// </summary>
    private static bool IsSynchronousArithmeticOperator(string @operator)
        => @operator is "+" or "-" or "*" or "/" or "//" or "%" or "**"
            or "band" or "bor" or "bxor" or "shl" or "shr";
}

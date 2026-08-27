using System.Collections;
using System.Numerics;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class TruthinessTests
{
    [Fact]
    public void Runtime_interpreter_and_compiler_wrappers_share_the_canonical_matrix()
    {
        foreach (var (name, value, expected) in CanonicalCases())
        {
            Assert.True(
                ToshTruthiness.IsTruthy(value) == expected,
                $"{name}: ToshTruthiness returned {!expected}.");
            Assert.True(
                OperatorEvaluator.ToBoolean(value) == expected,
                $"{name}: OperatorEvaluator returned {!expected}.");
            Assert.True(
                global::Tosh.Compiler.Runtime.ToshHost.IsTruthy(value) == expected,
                $"{name}: ToshHost returned {!expected}.");
        }
    }

    [Fact]
    public void General_enumerable_truthiness_probes_once_and_disposes_the_enumerator()
    {
        var empty = new TrackingEnumerable(hasValue: false);
        var populated = new TrackingEnumerable(hasValue: true);

        Assert.False(ToshTruthiness.IsTruthy(empty));
        Assert.True(ToshTruthiness.IsTruthy(populated));

        Assert.Equal(1, empty.MoveNextCount);
        Assert.Equal(1, empty.DisposeCount);
        Assert.Equal(1, populated.MoveNextCount);
        Assert.Equal(1, populated.DisposeCount);
    }

    [Fact]
    public void Explicit_boolean_conversion_remains_distinct_from_truthiness()
    {
        Assert.True(TypeConversion.TryConvert("false", typeof(bool), out var converted));
        Assert.Equal(false, converted);
        Assert.True(ToshTruthiness.IsTruthy("false"));
    }

    [Fact]
    public async Task Interpreter_conditions_follow_the_canonical_matrix()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            if (null) { echo bad } else { echo null-false }
            if (false) { echo bad } else { echo bool-false }
            if (0) { echo bad } else { echo zero-false }
            if (System.Double.NaN) { echo bad } else { echo nan-false }
            if ("") { echo bad } else { echo empty-string-false }
            if ("false") { echo nonempty-string-true } else { echo bad }
            if ([]) { echo bad } else { echo empty-list-false }
            if ([0]) { echo nonempty-list-true } else { echo bad }
            """);

        Assert.Equal(
            [
                "null-false",
                "bool-false",
                "zero-false",
                "nan-false",
                "empty-string-false",
                "nonempty-string-true",
                "empty-list-false",
                "nonempty-list-true",
            ],
            results);
    }

    [Fact]
    public async Task Logical_operators_use_truthiness_and_return_booleans()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            echo (not 0)
            echo (0 or 2)
            echo (2 and "value")
            echo (System.Double.NaN or false)
            echo ("" or [])
            """);

        Assert.Equal([true, true, true, false, false], results);
    }

    [Fact]
    public async Task Match_and_comprehension_guards_use_truthiness()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            echo (match (3) {
                3 if ((0)) => "bad"
                3 if (("set")) => "match-truthy"
                default => "bad"
            })
            echo [$x <| for x in [0, 1, 2] where $x]
            """);

        Assert.Equal("match-truthy", results[0]);
        Assert.Equal([1, 2], Assert.IsType<int[]>(results[1]));
    }

    [Fact]
    public async Task Event_when_guards_use_truthiness()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync(
            """
            func skip(evt) handles TruthinessEvent when { 0 } {
                return "bad"
            }
            func run(evt) handles TruthinessEvent when { "set" } {
                return "event-truthy"
            }
            """);

        var raised = await runtime.Events.RaiseAsync(
            new ShellEvent(
                "TruthinessEvent",
                new ShellEventSender("test", null, null)),
            CancellationToken.None);

        Assert.Equal(2, raised.HandlersInvoked);
        Assert.Equal([null, "event-truthy"], raised.Results);
    }

    [Fact]
    public async Task Standard_library_predicates_use_truthiness()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            echo 0 1 2 | where { _ }
            echo 0 0 3 | any { _ }
            echo 1 2 3 | all { _ }
            echo 1 0 3 | all { _ }
            """);

        Assert.Equal([1, 2, true, true, false], results);
    }

    [Fact]
    public async Task Assert_accepts_truthy_values_and_rejects_falsy_values()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        Assert.Empty(await engine.ExecuteToListAsync("""assert { "false" }"""));

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("assert { 0 }"));

        Assert.Equal(
            "tosh.runtime.assertion_failed",
            Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public async Task Refinement_predicates_remain_strictly_boolean()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                type BooleanOnly = int where _
                var value: BooleanOnly = 1
                """));

        Assert.Equal(
            "tosh.runtime.refinement_requires_boolean",
            Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public async Task Refinement_coercion_guards_use_broad_truthiness()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            type NormalizedString = string {
                if "apply" coerce "normalized"
                where _ == "normalized"
            }
            var value: NormalizedString = "input"
            echo $value
            """);

        Assert.Equal(["normalized"], results);
    }

    private static IEnumerable<(string Name, object? Value, bool Expected)> CanonicalCases()
    {
        yield return ("null", null, false);
        yield return ("false", false, false);
        yield return ("true", true, true);

        yield return ("byte zero", (byte)0, false);
        yield return ("byte nonzero", (byte)1, true);
        yield return ("sbyte zero", (sbyte)0, false);
        yield return ("short zero", (short)0, false);
        yield return ("ushort nonzero", (ushort)1, true);
        yield return ("int zero", 0, false);
        yield return ("int negative", -1, true);
        yield return ("uint nonzero", 1U, true);
        yield return ("long zero", 0L, false);
        yield return ("ulong nonzero", 1UL, true);
        yield return ("Int128 zero", (Int128)0, false);
        yield return ("UInt128 nonzero", (UInt128)1, true);
        yield return ("native int zero", IntPtr.Zero, false);
        yield return ("native uint nonzero", new UIntPtr(1), true);
        yield return ("big integer zero", BigInteger.Zero, false);
        yield return ("big integer nonzero", BigInteger.One, true);

        yield return ("Half zero", (Half)0, false);
        yield return ("Half NaN", Half.NaN, false);
        yield return ("Half nonzero", (Half)1, true);
        yield return ("float negative zero", -0f, false);
        yield return ("float NaN", float.NaN, false);
        yield return ("float infinity", float.PositiveInfinity, true);
        yield return ("double zero", 0d, false);
        yield return ("double NaN", double.NaN, false);
        yield return ("double infinity", double.NegativeInfinity, true);
        yield return ("decimal zero", 0m, false);
        yield return ("decimal nonzero", 0.1m, true);
        yield return ("complex zero", Complex.Zero, false);
        yield return ("complex NaN", new Complex(double.NaN, 0), false);
        yield return ("complex nonzero", Complex.One, true);

        yield return ("empty string", string.Empty, false);
        yield return ("nonempty string", "false", true);
        yield return ("empty array", Array.Empty<object?>(), false);
        yield return ("nonempty array", new object?[] { null }, true);
        yield return ("empty list", new List<int>(), false);
        yield return ("nonempty list", new List<int> { 0 }, true);
        yield return ("empty dictionary", new Dictionary<string, object?>(), false);
        yield return (
            "nonempty dictionary",
            new Dictionary<string, object?> { ["value"] = null },
            true);
        yield return (
            "empty general enumerable",
            Enumerable.Empty<int>().Where(static _ => true),
            false);
        yield return (
            "nonempty general enumerable",
            Enumerable.Range(0, 1).Where(static _ => true),
            true);

        yield return ("arbitrary object", new object(), true);
        yield return ("zero character", '\0', true);
        yield return ("default date", default(DateTime), true);
    }

    private sealed class TrackingEnumerable(bool hasValue) : IEnumerable
    {
        public int MoveNextCount { get; private set; }

        public int DisposeCount { get; private set; }

        public IEnumerator GetEnumerator() => new Enumerator(this, hasValue);

        private sealed class Enumerator(
            TrackingEnumerable owner,
            bool hasValue) : IEnumerator, IDisposable
        {
            private bool _moved;

            public object Current => 42;

            public bool MoveNext()
            {
                owner.MoveNextCount++;
                if (_moved)
                {
                    return false;
                }

                _moved = true;
                return hasValue;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose() => owner.DisposeCount++;
        }
    }
}

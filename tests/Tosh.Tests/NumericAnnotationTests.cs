using System.Numerics;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Annotating a numeric with the width you meant — <c>TOAST-0138</c>.
/// </summary>
/// <remarks>
/// <para>
/// The checker's assignability rule asked only about *widening*, so every narrowing
/// annotation warned <c>tosh.type.mismatch</c> — and then the runtime performed the
/// conversion and produced the right value. Narrowing is the ordinary reason to annotate a
/// numeric, so the rule fired on the code it exists to serve, which teaches a reader to
/// ignore the category that would catch a real mistake.
/// </para>
/// <para>
/// Whether a narrowing conversion succeeds depends on the value rather than the types — the
/// runtime takes <c>byte = 5</c> and refuses <c>byte = 300</c> — so a check over types alone
/// cannot separate them and does not try. That is the same conclusion <c>TS-P2-84</c> reached
/// for barewords.
/// </para>
/// </remarks>
public sealed class NumericAnnotationTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public NumericAnnotationTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var unit = Lowerer.Lower(engine.Parse(source, "<numeric-annotation-test>"), _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private static async Task<object?> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    private static bool Mismatches(IEnumerable<ToshDiagnostic> diagnostics) =>
        diagnostics.Any(d => d.Code == "tosh.type.mismatch");

    [Theory]
    [InlineData("var x: float = 1.5")]
    [InlineData("var x: byte = 5")]
    [InlineData("var x: short = 5")]
    [InlineData("var x: sbyte = 5")]
    [InlineData("var x: Half = 1.5")]
    [InlineData("var x: Int128 = 5")]
    [InlineData("var x: UInt128 = 5")]
    [InlineData("var x: nint = 5")]
    public void A_narrowing_numeric_annotation_does_not_warn(string source)
        => Assert.False(Mismatches(Check(source)), source);

    /// <summary>The widening rows are the regression guard: they were always clean.</summary>
    [Theory]
    [InlineData("var x: decimal = 1.5")]
    [InlineData("var x: long = 5")]
    [InlineData("var x: double = 5")]
    [InlineData("var x: uint = 5")]
    public void A_widening_numeric_annotation_stays_clean(string source)
        => Assert.False(Mismatches(Check(source)), source);

    /// <summary>
    /// A conversion that is not numeric at all still warns. Relaxing the rule to
    /// numeric-to-numeric must not relax it to anything-to-anything.
    /// </summary>
    [Theory]
    [InlineData("var x: byte = \"s\"")]
    [InlineData("var x: int = [1, 2]")]
    public void A_non_numeric_assignment_still_warns(string source)
        => Assert.True(Mismatches(Check(source)), source);

    [Theory]
    [InlineData("var x: float = 1.5\nreturn $x", 1.5f)]
    [InlineData("var x: byte = 5\nreturn $x", (byte)5)]
    [InlineData("var x: short = 5\nreturn $x", (short)5)]
    public async Task The_runtime_performs_the_conversion_the_checker_now_allows(string source, object expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A value that cannot fit is still refused — by the runtime, which knows the value.
    /// </summary>
    [Theory]
    [InlineData("var x: byte = 300")]
    [InlineData("var x: byte = 1.5")]
    [InlineData("var x: int = 1.9")]
    public async Task A_value_that_cannot_fit_is_still_refused(string source)
        => await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(source));

    // ── Literals wider than 64 bits ───────────────────────────────────────────

    /// <summary>
    /// A decimal literal wider than 64 bits is a value, not a mistake.
    /// </summary>
    /// <remarks>
    /// It raised <c>tosh.parser.numeric_literal_overflow</c> in the lexer, before any
    /// annotation was consulted, and the help line advised computing it "at runtime where a
    /// wider numeric type applies" — the language declining to express a value it has a type
    /// for. `Int128`, `UInt128` and `bigint` are all nameable, and none of them could be
    /// written down.
    /// </remarks>
    [Fact]
    public async Task A_decimal_literal_wider_than_64_bits_is_a_big_integer()
        => Assert.Equal(
            BigInteger.Parse("170141183460469231731687303715884105727"),
            await RunAsync("return 170141183460469231731687303715884105727"));

    [Fact]
    public async Task The_widest_int128_can_be_written()
        => Assert.Equal(
            Int128.MaxValue,
            await RunAsync("var y: Int128 = 170141183460469231731687303715884105727\nreturn $y"));

    [Fact]
    public async Task The_widest_uint128_can_be_written()
        => Assert.Equal(
            UInt128.MaxValue,
            await RunAsync("var y: UInt128 = 340282366920938463463374607431768211455\nreturn $y"));

    /// <summary>And one that does not fit the annotation is still refused.</summary>
    [Fact]
    public async Task A_literal_too_wide_for_its_annotation_is_refused()
        => await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "var y: Int128 = 9999999999999999999999999999999999999999999999999\nreturn $y"));

    /// <summary>
    /// A suffixed or non-decimal literal still overflows: each states the width it meant, and
    /// widening it would overrule the author rather than serve them.
    /// </summary>
    [Theory]
    [InlineData("var x = 99999999999999999999999999L")]
    [InlineData("var x = 0xFFFFFFFFFFFFFFFFFF")]
    public async Task A_literal_that_states_its_width_still_overflows(string source)
        => await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(source));

    [Fact]
    public async Task An_ordinary_suffixed_literal_is_unchanged()
        => Assert.Equal(100L, await RunAsync("return 100L"));

    // ── `TOAST-0139`: a wide literal nothing vouched for ──────────────────────

    private IReadOnlyList<ToshDiagnostic> CheckWith(string source) => Check(source);

    private static bool Wide(IEnumerable<ToshDiagnostic> diagnostics) =>
        diagnostics.Any(d => d.Code == "tosh.type.wide_integer_literal");

    /// <summary>
    /// An annotation that can hold the width vouches for it, and nothing is said.
    /// </summary>
    [Theory]
    [InlineData("var y: Int128 = 170141183460469231731687303715884105727")]
    [InlineData("var y: UInt128 = 340282366920938463463374607431768211455")]
    [InlineData("var b: bigint = 99999999999999999999999")]
    public void An_annotated_wide_literal_is_not_questioned(string source)
        => Assert.False(Wide(CheckWith(source)), source);

    /// <summary>
    /// With nothing to vouch for it, a literal too wide for any 64-bit type is far more
    /// often a typed digit too many than an intention.
    /// </summary>
    /// <remarks>
    /// This is the half of the literal-widening change that keeps it from being silent. The
    /// lexer produces the value so it can be expressed at all; noticing that nobody asked for
    /// that width is a separate question, with different information available.
    /// </remarks>
    [Theory]
    [InlineData("var x = 99999999999999999999999")]
    [InlineData("echo 99999999999999999999999")]
    [InlineData("99999999999999999999999")]
    public void An_unvouched_wide_literal_is_questioned(string source)
        => Assert.True(Wide(CheckWith(source)), source);

    /// <summary>
    /// The inferred type does not count as vouching. Without a *written* annotation the
    /// declared type is whatever the inferrer read off the literal — `BigInteger` for a wide
    /// one — so the literal would vouch for itself and the warning could never fire.
    /// </summary>
    [Fact]
    public void An_inferred_type_does_not_vouch_for_the_literal_that_produced_it()
        => Assert.True(Wide(CheckWith("var x = 99999999999999999999999")));

    [Theory]
    [InlineData("var x = 5")]
    [InlineData("var x = 9223372036854775807")]
    [InlineData("var s = \"hello\"")]
    [InlineData("var x: bigint = 5")]
    public void An_ordinary_literal_is_untouched(string source)
        => Assert.False(Wide(CheckWith(source)), source);

    // ── The conversion failure says what it got ───────────────────────────────

    /// <summary>
    /// A failed annotation conversion names the type it was handed.
    /// </summary>
    /// <remarks>
    /// It used to say only "a value could not be converted", which is the one thing the reader
    /// already knows. Two adjacent fields annotated `System.DateOnly` and `System.TimeOnly`,
    /// constructed in the wrong order, reported that a value could not become a `DateOnly` —
    /// true, and silent about the `TimeOnly` that names the mistake at a glance. Found on a
    /// real script, where the swap took a full publish run to locate.
    /// </remarks>
    [Fact]
    public async Task A_failed_conversion_names_the_type_it_was_given()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "record R(StartDate: System.DateOnly, StartTime: System.TimeOnly)\n"
            + "var d: System.DateOnly = date -d now\n"
            + "var t: System.TimeOnly = date -t now\n"
            + "new R($t, $d)"));

        Assert.Contains("System.TimeOnly", error.Message, StringComparison.Ordinal);
        Assert.Contains("System.DateOnly", error.Message, StringComparison.Ordinal);
    }

    /// <summary>And the right order is accepted, which is what makes the message a diagnosis.</summary>
    [Fact]
    public async Task The_same_record_accepts_the_arguments_in_order()
        => await RunAsync(
            "record R(StartDate: System.DateOnly, StartTime: System.TimeOnly)\n"
            + "var d: System.DateOnly = date -d now\n"
            + "var t: System.TimeOnly = date -t now\n"
            + "new R($d, $t)");
}

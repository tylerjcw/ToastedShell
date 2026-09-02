using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A variant pattern qualified by the union that declares it — <c>TOAST-0095</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Some(v)</c> matched and <c>Maybe.Some(v)</c> did not. The matcher compared the pattern's
/// whole name against the variant name, so every qualified pattern failed and the arm fell
/// through to <c>default</c> — no diagnostic, no match, and an arm that reads as live.
/// </para>
/// <para>
/// That is worse in <c>match</c> than it would be anywhere else. The construct exists to
/// dispatch exhaustively, and the binder's exhaustiveness check keyed on the same bare name, so
/// it did not merely miscount qualified arms — it bailed out entirely and said nothing. A match
/// written in qualified arms was neither judged exhaustive nor reported incomplete.
/// </para>
/// <para>
/// Found while probing <c>TOAST-0083</c>, which is the reason it mattered enough to fix first:
/// <c>Option</c> and <c>Result</c> are meant to be *core* types, so <c>Result.Ok(v)</c> is the
/// spelling their users would reach for, and every such arm was silently dead.
/// </para>
/// </remarks>
public sealed class QualifiedVariantPatternTests
{
    private const string Prelude =
        """
        union QvpMaybe { Some(v) None() }
        union QvpOther { Some(v) }
        union QvpOpt<T> { Some(T) None() }

        """;

    /// <summary>
    /// Runs with the binder strict, the way the CLI runs a script — exhaustiveness is reported
    /// at bind time, and under the default <c>Warn</c> it is written to the error stream rather
    /// than thrown, so a test would pass whether or not the check existed.
    /// </summary>
    private static async Task<string> RunStrictAsync(string source)
    {
        var engine = ShellEngine.CreateFullShell();
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);
        var results = await engine.ExecuteToListAsync(Prelude + source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(Prelude + source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── Matching ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("QvpMaybe.Some(v)")]
    [InlineData("QvpMaybe::Some(v)")]
    [InlineData("Some(v)")]
    public async Task A_variant_matches_however_its_pattern_is_qualified(string pattern)
    {
        Assert.Equal("5", await RunAsync(
            $$"""
            var m = QvpMaybe.Some(5)
            echo (match ($m) {
                {{pattern}} => $v
                default => 0
            })
            """));
    }

    [Fact]
    public async Task A_qualified_payload_less_variant_matches()
    {
        Assert.Equal("empty", await RunAsync(
            """
            var m = QvpMaybe.None()
            echo (match ($m) {
                QvpMaybe.None() => "empty"
                default => "other"
            })
            """));
    }

    [Fact]
    public async Task A_generic_union_takes_a_qualified_pattern()
    {
        Assert.Equal("5", await RunAsync(
            """
            var o = QvpOpt.Some(5)
            echo (match ($o) {
                QvpOpt::Some(v) => $v
                default => 0
            })
            """));
    }

    [Fact]
    public async Task A_variant_that_does_not_match_still_falls_through_quietly()
    {
        // The fix must not turn "this arm is not the one" into a diagnostic; only a *wrong
        // qualifier* is a mistake in the pattern.
        Assert.Equal("99", await RunAsync(
            """
            var m = QvpMaybe.None()
            echo (match ($m) {
                QvpMaybe.Some(v) => $v
                default => 99
            })
            """));
    }

    // ── The wrong union is a mistake, not a miss ───────────────────────────────

    [Fact]
    public async Task A_qualifier_naming_another_union_is_a_diagnostic()
    {
        // `QvpOther` also declares a `Some`, so this is the case where silence would be most
        // convincing and most wrong.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            var m = QvpMaybe.Some(5)
            echo (match ($m) {
                QvpOther.Some(v) => $v
                default => 0
            })
            """));

        Assert.Contains("QvpOther", error.Message, StringComparison.Ordinal);
        Assert.Contains("QvpMaybe", error.Message, StringComparison.Ordinal);
    }

    // ── Exhaustiveness counts qualified arms ───────────────────────────────────

    [Fact]
    public async Task Qualified_arms_cover_their_variants()
    {
        Assert.Equal("5", await RunAsync(
            """
            var m = QvpMaybe.Some(5)
            echo (match ($m) {
                QvpMaybe.Some(v) => $v
                QvpMaybe.None() => 0
            })
            """));
    }

    [Fact]
    public async Task Mixed_bare_and_qualified_arms_cover_their_variants()
    {
        Assert.Equal("5", await RunAsync(
            """
            var m = QvpMaybe.Some(5)
            echo (match ($m) {
                Some(v) => $v
                QvpMaybe::None() => 0
            })
            """));
    }

    [Fact]
    public async Task An_incomplete_qualified_match_is_still_reported()
    {
        // The half that made the old behaviour dangerous: not merely that qualified arms did not
        // match, but that the checker went quiet about the ones that were missing.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunStrictAsync(
            """
            var m = QvpMaybe.Some(5)
            echo (match ($m) {
                QvpMaybe.Some(v) => $v
            })
            """));

        Assert.Contains("None", error.Message, StringComparison.Ordinal);
    }
}

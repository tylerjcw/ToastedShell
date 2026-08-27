using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A ternary arm may invoke a multi-value command — <c>TS-P2-73</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reported from a profile alias:
/// <c>func svc(action, service) =&gt; ($action == journal) ? (sudo journalctl -u $service
/// -n 20) : (sudo systemctl $action $service)</c> failed with
/// <c>tosh.runtime.subexpression_requires_single_value</c> — "this subexpression produced
/// 20 values" — while the identical <c>if</c>/<c>else</c> block streamed all twenty.
/// </para>
/// <para>
/// The position was inescapable. Unparenthesised arms are a *parse* error, so parentheses
/// are mandatory in a ternary; and parenthesising is what makes a grouped pipeline a
/// single-value context. Parentheses meant two things at once — grouping and collapse —
/// and an arm needs only the first.
/// </para>
/// <para>
/// <b>The <c>TS-P1-20</c> rule is unchanged.</b> A pipeline used where one value is
/// genuinely required still collapses: none to <c>null</c>, one to the item, more than one
/// is a failure rather than a silent collection. That is what stops
/// <c>var n = ([1, 2, 3] | count)</c> becoming a one-element list. What changed is scope —
/// a ternary that *is* a pipeline stage streams, exactly as <c>for x in (pipeline)</c>
/// already did under rule 3 of that same list.
/// </para>
/// </remarks>
public sealed class TernaryArmMultiplicityTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    // ── The reported case ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_ternary_arm_streams_every_value_it_produces()
    {
        var results = await RunAsync(
            """
            func pick(a: string) => ($a == "x") ? (seq 1 3) : (seq 7 9)
            pick "x"
            """);

        Assert.Equal(["1", "2", "3"], results.Select(v => v?.ToString()));
    }

    [Fact]
    public async Task The_other_arm_streams_too()
    {
        // Both arms, or the fix is one branch of an if.
        var results = await RunAsync(
            """
            func pick(a: string) => ($a == "x") ? (seq 1 3) : (seq 7 9)
            pick "y"
            """);

        Assert.Equal(["7", "8", "9"], results.Select(v => v?.ToString()));
    }

    [Fact]
    public async Task It_matches_what_the_block_form_already_did()
    {
        // The block spelling is the reference: the reporter had it commented out above
        // the one-liner, working. The two must now agree.
        var ternary = await RunAsync(
            """
            func pick(a: string) => ($a == "x") ? (seq 1 3) : (seq 7 9)
            pick "x"
            """);

        var block = await RunAsync(
            """
            func pick(a: string) { if ($a == "x") { seq 1 3 } else { seq 7 9 } }
            pick "x"
            """);

        Assert.Equal(block.Select(v => v?.ToString()), ternary.Select(v => v?.ToString()));
    }

    [Fact]
    public async Task A_nested_ternary_resolves_to_the_arm_it_selects()
    {
        var results = await RunAsync(
            """
            func pick(a: string) => ($a == "x") ? (seq 1 2) : (($a == "y") ? (seq 5 6) : (seq 8 9))
            pick "y"
            """);

        Assert.Equal(["5", "6"], results.Select(v => v?.ToString()));
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Fact]
    public async Task A_scalar_arm_is_unchanged()
    {
        var results = await RunAsync("""echo (true ? "a" : "b")""");

        Assert.Equal("a", Assert.Single(results)?.ToString());
    }

    [Theory]
    // Positions where a single value is genuinely required still refuse several, which is
    // the whole point of TS-P1-20 and must not be weakened by this change.
    [InlineData("""echo (true ? (seq 1 3) : (seq 7 9))""")]
    [InlineData("""var x = (true ? (seq 1 3) : (seq 7 9))""")]
    public async Task A_value_context_still_refuses_more_than_one(string script)
    {
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(() => RunAsync(script));

        Assert.Contains(
            error.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.runtime.subexpression_requires_single_value");
    }

    [Fact]
    public async Task The_collapse_that_motivated_the_rule_still_collapses()
    {
        // `var n = ([1,2,3] | count)` must be 3, not a one-element list. This is the case
        // TS-P1-20 exists for, and the reason the rule was not simply relaxed.
        var results = await RunAsync(
            """
            var n = ([1, 2, 3] | count)
            $n
            """);

        Assert.Equal(3, Assert.Single(results));
    }

    [Fact]
    public async Task An_iteration_source_still_receives_every_item()
    {
        var results = await RunAsync(
            """
            var total = 0
            for x in (seq 1 4) { $total = ($total + ($x | cast int)) }
            $total
            """);

        Assert.Equal(10, Assert.Single(results));
    }
}

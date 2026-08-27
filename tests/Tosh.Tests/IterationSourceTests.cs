using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A <c>for</c> iteration source is a whole expression — <c>TS-P2-77</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ParseParenthesizedPipeline</c> took its parenthesised branch whenever the source
/// began with <c>(</c>, so it consumed <c>(1)</c> out of <c>for i in (1) .. 3 { … }</c>,
/// returned, and left the caller expecting <c>{</c> where <c>..</c> stood. The report was
/// <c>expected_block</c> — a message about the loop body for a defect in the source.
/// </para>
/// <para>
/// The branch is now taken only when the group <em>is</em> the whole source, decided by
/// scanning for the matching parenthesis rather than guessing from the opener. Wrapping
/// the whole range already worked and still does; what was rejected was only the shorter
/// spelling.
/// </para>
/// </remarks>
public sealed class IterationSourceTests
{
    private static async Task<int> CountAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var results = await engine.ExecuteToListAsync(script + "\n$__n");
        return Convert.ToInt32(Assert.Single(results));
    }

    [Theory]
    // A parenthesised *operand* — the shape that failed.
    [InlineData("for i in (1) .. 3 { $__n = ($__n + 1) }", 3)]
    [InlineData("for i in (1 + 1) .. 4 { $__n = ($__n + 1) }", 3)]
    [InlineData("for i in 1 .. (1 + 2) { $__n = ($__n + 1) }", 3)]
    public async Task A_parenthesised_operand_does_not_end_the_source(string loop, int expected)
    {
        Assert.Equal(expected, await CountAsync("var __n = 0\n" + loop));
    }

    [Theory]
    // The forms that already worked and must keep working: the whole source
    // parenthesised, a bare range, a variable, and a command.
    [InlineData("for i in ((1) .. 3) { $__n = ($__n + 1) }", 3)]
    [InlineData("for i in 1 .. 3 { $__n = ($__n + 1) }", 3)]
    [InlineData("for i in (seq 1 4) { $__n = ($__n + 1) }", 4)]
    public async Task The_forms_that_already_worked_still_do(string loop, int expected)
    {
        Assert.Equal(expected, await CountAsync("var __n = 0\n" + loop));
    }

    [Fact]
    public async Task A_parenthesised_variable_source_still_iterates_its_items()
    {
        Assert.Equal(
            2,
            await CountAsync(
                """
                var __n = 0
                var xs = [1, 2]
                for i in ($xs) { $__n = ($__n + 1) }
                """));
    }

    [Fact]
    public async Task A_computed_bound_reads_as_written()
    {
        // `TS-P2-76` and this item together: the natural spelling of a computed range
        // bound in a loop, which needed both the precedence level and this lookahead.
        Assert.Equal(
            4,
            await CountAsync(
                """
                var __n = 0
                var n = 3
                for i in 1 .. $n + 1 { $__n = ($__n + 1) }
                """));
    }
}

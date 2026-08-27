using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Covers the two decisions that <c>HasTopLevelPipeBeforeCloseParen</c> drives:
/// whether a parenthesised group containing a top-level <c>|</c> is parsed as a
/// pipeline or as something else.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the helper's <c>true</c> branch had no coverage at all.
/// Stubbing it to <c>return false</c> left the whole suite passing at 3,383,
/// which means <c>if (ls | count)</c> could have been broken outright without a
/// single test noticing — and <c>TS-P2-24</c> plans to replace this helper with a
/// structural-pass query, a refactor that would have been unverifiable.
/// </para>
/// <para>
/// Every case here fails when the helper returns a constant, which is the
/// property that makes them useful to that refactor.
/// </para>
/// </remarks>
public sealed class PipelineInParenthesesTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        return await engine.ExecuteToListAsync(source);
    }

    [Theory]
    // A pipeline in an if-condition must be evaluated as a pipeline, not read as
    // an operator expression that happens to contain `|`.
    [InlineData("if ([1, 2, 3] | any { $_ > 2 }) { echo yes } else { echo no }", "yes")]
    [InlineData("if ([1, 2, 3] | any { $_ > 9 }) { echo yes } else { echo no }", "no")]
    // Truthiness of a pipeline result, which is TS-P1-01's rule reached through
    // this branch.
    [InlineData("if ([1, 2, 3] | count) { echo nonempty } else { echo empty }", "nonempty")]
    [InlineData("if ([] | count) { echo nonempty } else { echo empty }", "empty")]
    // Multi-stage, so the condition cannot be mistaken for a single command.
    [InlineData("if ([3, 1, 2] | sort | first) { echo first } else { echo none }", "first")]
    public async Task A_pipeline_in_an_if_condition_is_parsed_as_a_pipeline(
        string source,
        string expected)
    {
        Assert.Equal(expected, Assert.Single(await RunAsync(source))?.ToString());
    }

    [Fact]
    public async Task A_condition_without_a_pipe_still_parses_as_an_expression()
    {
        // The false branch has to keep working too: the helper chooses between
        // two readings, so pinning only one of them would let the other rot.
        Assert.Equal("big", Assert.Single(await RunAsync(
            "if (2 + 2 > 3) { echo big } else { echo small }"))?.ToString());

        Assert.Equal("small", Assert.Single(await RunAsync(
            "if (2 + 2 > 9) { echo big } else { echo small }"))?.ToString());
    }

    [Fact]
    public async Task A_parenthesised_predicate_without_a_pipe_reads_as_a_predicate()
    {
        // The other call site: in implicit-current-item position a parenthesised
        // group with no top-level `|` is a `where` predicate over `$_`.
        var results = await RunAsync("[1, 2, 3] | where ($_ > 1) | count");
        Assert.Equal("2", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task While_conditions_take_the_same_path()
    {
        var results = await RunAsync(
            """
            var n = 0
            while (([1, 2, 3] | count) > $n) {
                $n = $n + 1
            }
            echo $n
            """);

        Assert.Equal("3", Assert.Single(results)?.ToString());
    }
}

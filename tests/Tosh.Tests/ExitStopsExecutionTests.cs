using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>exit</c> stops the work, not just the session — <c>TS-P2-52</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>echo one</c> / <c>exit 0</c> / <c>echo two</c> printed both lines. <c>RequestExit</c>
/// recorded an exit code and set a flag that only the REPL loop ever read, so a script using
/// <c>exit</c> for an early return carried on doing exactly what it meant to skip.
/// </para>
/// <para>
/// The check is made in two loops rather than one. Fixing only the block executor left a plain
/// script running as before, because the top level iterates its own statements instead of going
/// through it — which is the sort of second copy this programme keeps finding, and here it was
/// found by the fix visibly not working rather than by reading.
/// </para>
/// </remarks>
public sealed class ExitStopsExecutionTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    [Fact]
    public async Task Statements_after_exit_do_not_run()
    {
        Assert.Equal("one", await RunAsync("\"one\"\nexit 0\n\"two\""));
    }

    [Fact]
    public async Task Exit_inside_a_function_stops_the_script()
    {
        // The function returning is not enough: the script that called it must stop too.
        Assert.Equal("a", await RunAsync("func f() { exit 0 }\n\"a\"\nf\n\"b\""));
    }

    [Fact]
    public async Task Exit_inside_a_loop_stops_the_loop_and_the_script()
    {
        Assert.Equal("1,2", await RunAsync(
            """
            for i in [1, 2, 3] {
                $i
                if ($i == 2) { exit 0 }
            }
            "after"
            """));
    }

    [Fact]
    public async Task Exit_inside_a_branch_stops_execution()
    {
        Assert.Equal("before", await RunAsync("\"before\"\nif (true) { exit 0 }\n\"after\""));
    }

    [Theory]
    // The control: without `exit`, every statement still runs. A check that stops too eagerly
    // would pass every case above and break all ordinary scripts.
    [InlineData("\"one\"\n\"two\"", "one,two")]
    [InlineData("for i in [1, 2, 3] { $i }", "1,2,3")]
    [InlineData("func f() { return 1 }\nf\n\"after\"", "1,after")]
    public async Task A_script_without_exit_runs_to_the_end(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }
}

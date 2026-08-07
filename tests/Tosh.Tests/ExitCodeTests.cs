using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>exit &lt;n&gt;</c> leaves the recorded status at <c>n</c> — <c>TS-P2-56</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>exit &lt;n&gt;</c> reported success. Two independent causes, and fixing either alone
/// left the behaviour unchanged, which is why both are pinned here.
/// </para>
/// <list type="bullet">
/// <item>
/// <c>ExitCommand</c> matched its argument as <c>is string</c>, but <c>exit 3</c> passes the
/// number 3 rather than the text "3", so the branch never ran and no code was recorded at all.
/// </item>
/// <item>
/// The pipeline's exit-status tracker then wrote the status of the pipeline that had just run.
/// <c>exit 3</c> is itself a command that succeeds, so it overwrote the 3 with a 0.
/// </item>
/// </list>
/// <para>
/// Confirmed against the stable binary before starting: this predated the <c>TS-P2-52</c> work on
/// whether <c>exit</c> stops execution, and is a separate question from it.
/// </para>
/// </remarks>
public sealed class ExitCodeTests
{
    private static async Task<int> ExitCodeAfterAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        await new ToshEngine(runtime).ExecuteToListAsync(source);
        return runtime.LastExitCode;
    }

    [Theory]
    [InlineData("exit 3", 3)]
    [InlineData("exit 0", 0)]
    [InlineData("exit 42", 42)]
    public async Task An_exit_code_is_recorded(string source, int expected)
    {
        Assert.Equal(expected, await ExitCodeAfterAsync(source));
    }

    [Fact]
    public async Task An_exit_code_survives_the_statement_that_follows_it()
    {
        // The tracker overwrote the code when the pipeline it belonged to finished. Since `exit`
        // succeeds, that reliably replaced any code with zero.
        Assert.Equal(3, await ExitCodeAfterAsync("\"before\"\nexit 3\n\"after\""));
    }

    [Fact]
    public async Task An_exit_code_set_inside_a_function_is_kept()
    {
        Assert.Equal(4, await ExitCodeAfterAsync("func f() { exit 4 }\nf"));
    }

    [Fact]
    public async Task An_exit_code_set_inside_a_loop_is_kept()
    {
        Assert.Equal(7, await ExitCodeAfterAsync("for i in [1, 2, 3] { if ($i == 2) { exit 7 } }"));
    }

    [Fact]
    public async Task A_bare_exit_reports_success()
    {
        Assert.Equal(0, await ExitCodeAfterAsync("exit"));
    }

    [Fact]
    public async Task A_script_without_exit_still_reports_success()
    {
        // The control: the guard must not freeze the status for scripts that never call `exit`.
        Assert.Equal(0, await ExitCodeAfterAsync("\"one\"\n\"two\""));
    }
}

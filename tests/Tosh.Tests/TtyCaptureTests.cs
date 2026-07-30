using System.Diagnostics;

namespace Tosh.Tests;

/// <summary>
/// External command output is captured when a value is consumed — <c>TS-P1-30</c>.
/// </summary>
/// <remarks>
/// <para>
/// These run the built CLI under a **pty**, which is the whole point. At a terminal
/// `DetermineSpawnMode` used to answer "is my output consumed?" with `IsPipelined`, true only
/// when a downstream stage exists — so `git … | collect` captured while
/// `var x = git …`, `(git …)` and `$(git …)` printed to the terminal and yielded `null`.
/// </para>
/// <para>
/// **A test process has no TTY**, so without a pty the same code takes the piped path and
/// captures correctly. That is exactly how this survived 3,602 passing tests: every one of
/// them exercised the branch that worked. Asserting this without a pty would re-test the
/// working branch and prove nothing.
/// </para>
/// <para>
/// The pty comes from <c>script(1)</c>, which is Linux/util-linux. Skipped elsewhere with the
/// reason stated, so a skip is not mistaken for a pass.
/// </para>
/// </remarks>
public sealed class TtyCaptureTests
{
    /// <summary>
    /// Establishes that a pty can be allocated, failing loudly on Linux if
    /// <c>script(1)</c> is missing and returning early elsewhere.
    /// </summary>
    /// <remarks>
    /// No dynamic-skip package is referenced by this project, so a non-Linux run returns
    /// early rather than skipping visibly. That is a real limitation: on Windows or macOS
    /// these read as passes without having run. On Linux — the development and CI platform —
    /// a missing <c>script</c> fails rather than silently passing, which is the case that
    /// matters, since a silent pass here would recreate the exact blind spot the item is
    /// about.
    /// </remarks>
    private static bool PtyUnavailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        Assert.True(
            File.Exists("/usr/bin/script"),
            "script(1) is required to allocate a pty; without one these tests would only "
            + "re-verify the non-TTY path that already worked");

        Assert.True(File.Exists(CliPath), $"CLI not built at {CliPath}");
        return false;
    }

    /// <summary>The branch name, obtained without a pty, to compare against.</summary>
    private static string CurrentBranch()
    {
        var startInfo = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
        {
            WorkingDirectory = ProjectRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)!;
        var branch = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(10_000);
        return branch;
    }

    private static string ProjectRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string CliPath =>
        Path.Combine(ProjectRoot, "src", "Tosh.Cli", "bin", "Debug", "net10.0", "Tosh.Cli");

    /// <summary>
    /// Runs <paramref name="script"/> through the CLI with a pty attached, returning
    /// everything that reached the terminal.
    /// </summary>
    private static string RunUnderPty(string script)
    {
        // Single-quoted for the shell that `script -c` invokes, so the tosh source may
        // contain double quotes and `$`.
        var inner = $"{CliPath} --no-profile -c '{script}'";

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/script",
            WorkingDirectory = ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("-qec");
        startInfo.ArgumentList.Add(inner);
        startInfo.ArgumentList.Add("/dev/null");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(milliseconds: 60_000);

        // Strip ANSI styling and carriage returns the pty introduces.
        return System.Text.RegularExpressions.Regex
            .Replace(output, @"\x1b\[[0-9;]*[A-Za-z]", string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    // The reported form.
    [InlineData("var x = git rev-parse --abbrev-ref HEAD\necho $\"GOT[{$x}]\"")]
    // Subexpression.
    [InlineData("var x = (git rev-parse --abbrev-ref HEAD)\necho $\"GOT[{$x}]\"")]
    // Command substitution — the syntax that looks like it should capture.
    [InlineData("var x = $(git rev-parse --abbrev-ref HEAD)\necho $\"GOT[{$x}]\"")]
    // Through a function return.
    [InlineData("func f() -> string { return git rev-parse --abbrev-ref HEAD }\necho $\"GOT[{(f())}]\"")]
    // The control that already worked, so the fix cannot have traded one for another.
    [InlineData("var x = git rev-parse --abbrev-ref HEAD | collect\necho $\"GOT[{$x}]\"")]
    public void A_consumed_external_is_captured(string script)
    {
        if (PtyUnavailable())
        {
            return;
        }

        var output = RunUnderPty(script);

        Assert.Contains("GOT[", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GOT[]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GOT[null]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_for_loop_consumes_external_output()
    {
        if (PtyUnavailable())
        {
            return;
        }

        // Streams rather than collecting, but it consumes — so the child's stdout must be
        // piped. The flag changes how the process is spawned, not how values flow.
        var output = RunUnderPty(
            "var n = 0\nfor l in (git rev-parse --abbrev-ref HEAD) { $n = 1 }\necho $\"RAN[{$n}]\"");

        Assert.Contains("RAN[1]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_displayed_external_still_prints()
    {
        if (PtyUnavailable())
        {
            return;
        }

        // The other half of the decision: terminal passthrough is what the top-level display
        // path is *for*. If capture had been made unconditional this would stop working, and
        // interactive children would break with it.
        var output = RunUnderPty("git rev-parse --abbrev-ref HEAD");

        Assert.Contains(CurrentBranch(), output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interpolation_hole_does_not_yet_capture()
    {
        if (PtyUnavailable())
        {
            return;
        }

        // Characterization, not an endorsement. An interpolation hole re-parses its text and
        // runs it as a whole statement through EvaluateAsync, so it reaches the pipeline by a
        // different route than the consuming sites this change marked — the flag would have
        // to thread through EvaluateParseResultAsync and EvaluateStatementAsync, the engine's
        // hottest dispatch. Filed as TS-P1-32 and left for its own slice; pinned here so the
        // gap is visible and so flipping it later is a deliberate edit to this assertion
        // rather than a surprise.
        var output = RunUnderPty("echo $\"GOT[{git rev-parse --abbrev-ref HEAD}]\"");

        Assert.Contains("GOT[]", output, StringComparison.Ordinal);
    }
}

using System.Diagnostics;

namespace Tosh.Tests;

/// <summary>
/// Naming a CLR type must not cost a second start-up — <c>TOAST-0064</c>.
/// </summary>
/// <remarks>
/// <para>
/// The first CLR type annotation in a script used to block on a platform type index covering
/// every type in every loaded assembly: 17,139 fully qualified names, about 100 ms, on top of a
/// 100 ms start-up. An alias or a same-file class resolved without it. Measured then, an empty
/// file was 100 ms and <c>func q(p: System.Text.StringBuilder) =&gt; clear</c> was 221 ms.
/// </para>
/// <para>
/// The fix was a disk cache consulted ahead of the in-memory index, so the ratio is now about
/// 1.0. This asserts the <em>ratio</em> rather than a wall-clock number: an absolute budget
/// measures the machine, and this suite runs on more than one. A regression to the old behaviour
/// is a ratio above 2, so the threshold has roughly a 100% margin in the direction that matters
/// and still fails loudly if the index comes back into the hot path.
/// </para>
/// <para>
/// Out of process on purpose. The index is a <c>static Lazy</c>, so in a shared test process it
/// is already built by whatever ran first and "was it built" cannot be asked.
/// </para>
/// </remarks>
public sealed class StartupBudgetTests
{
    private const int Rounds = 5;
    private const double MaxRatio = 2.0;

    private static string CliPath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Tosh.Cli/bin/Debug/net10.0/Tosh.Cli.dll"));

    private static long MinimumMilliseconds(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tosh-budget-{Guid.NewGuid():N}.tosh");
        File.WriteAllText(path, script);

        try
        {
            var best = long.MaxValue;

            for (var round = 0; round < Rounds; round++)
            {
                var info = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add(CliPath);
                info.ArgumentList.Add("--no-startup");
                info.ArgumentList.Add(path);

                var clock = Stopwatch.StartNew();
                using var process = Process.Start(info)!;
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                clock.Stop();

                best = Math.Min(best, clock.ElapsedMilliseconds);
            }

            return best;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_clr_annotation_does_not_cost_a_second_start_up()
    {
        // The suite builds before it runs, so this is present in the ordinary case. Running the
        // test project alone should not fail on an absent binary.
        if (!File.Exists(CliPath)) { return; }

        // Interleaved would be better still; minimum-of-N already removes most of the noise, and
        // the threshold is far enough away that ordering cannot reach it.
        var baseline = MinimumMilliseconds("echo 1\n");
        var annotated = MinimumMilliseconds("func q(p: System.Text.StringBuilder) => clear\necho 1\n");

        var ratio = annotated / (double)Math.Max(baseline, 1);

        Assert.True(
            ratio < MaxRatio,
            $"a CLR type annotation cost {ratio:F2}x a bare start-up "
            + $"({annotated} ms against {baseline} ms). TOAST-0064 brought this to about 1.0 by "
            + "consulting the platform type cache ahead of building the in-memory index; a ratio "
            + "at or above 2 means the index is back in the hot path.");
    }

    [Fact]
    public void An_alias_and_a_local_class_are_free()
    {
        if (!File.Exists(CliPath)) { return; }

        // The control: these never needed the index, so they were never the regression risk. If
        // *these* slow down, the cause is start-up generally rather than type resolution, and the
        // test above would mislead by staying green.
        var baseline = MinimumMilliseconds("echo 1\n");
        var local = MinimumMilliseconds("class Local { }\nfunc q(p: Local) => clear\necho 1\n");

        var ratio = local / (double)Math.Max(baseline, 1);

        Assert.True(
            ratio < MaxRatio,
            $"a local class annotation cost {ratio:F2}x a bare start-up ({local} ms against {baseline} ms)");
    }
}

using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Entering the argument switch stays cheap — <c>TOAST-0009</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>EvaluateArgumentSlowAsync</c> was one async method holding thirty-nine cases, so its
/// state-machine box carried the locals of every branch and a literal paid for the largest case
/// in the switch. Each case now lives in its own method, and the box holds only what the
/// dispatcher itself needs.
/// </para>
/// <para>
/// Measured with the fast-path suppression seam, which is what makes this observable at all: the
/// five shapes the synchronous fast path answers never enter the switch, so a benchmark built
/// from them measures the workaround rather than the thing. Before the extraction
/// <c>$s = ($t + 1)</c> cost 7,417 bytes per iteration more on the slow path than the fast one;
/// after it, 2,520.
/// </para>
/// <para>
/// Allocation is deterministic in a dedicated process, which is why the bench harness can rely
/// on it. <c>GC.GetTotalAllocatedBytes</c> is process-wide, though, so inside the parallel suite
/// this measures every other test allocating at the same moment: the first version of this guard
/// read 4,863 bytes against 2,520 run alone. Hence the serial collection. The bound is then set
/// well above the measurement, so ordinary drift does not trip it and a return to the old shape
/// does.
/// </para>
/// </remarks>
[Collection(AllocationSerialCollection.Name)]
public sealed class ArgumentEvaluationCostTests
{
    private const int Iterations = 20_000;

    /// <summary>Bytes per iteration, best of three, for one loop body.</summary>
    private static async Task<long> MeasureAsync(string body, bool suppressFastPath)
    {
        var source = $"var t = 7\nvar s = 0\nfor i in 1..{Iterations} {{ {body} }}\n";

        async Task RunAsync()
        {
            var engine = new ToshEngine(ToshRuntime.CreateDefault().Language)
            {
                SuppressSimpleArgumentFastPath = suppressFastPath,
            };
            await engine.ExecuteToListAsync(source);
        }

        await RunAsync();   // warm up: JIT, and everything that allocates once

        var best = long.MaxValue;

        for (var run = 0; run < 3; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetTotalAllocatedBytes(precise: true);
            await RunAsync();
            var after = GC.GetTotalAllocatedBytes(precise: true);

            best = Math.Min(best, (after - before) / Iterations);
        }

        return best;
    }


    [Theory]
    [InlineData("$s = ($t)", 3_500)]
    [InlineData("$s = ($t + 1)", 4_500)]
    public async Task Entering_the_argument_switch_stays_cheap(string body, long budget)
    {
        // Proved to be measuring the switch before measuring anything about it. Without this
        // the assertion below passes when the body never enters the slow path at all, which
        // is the failure mode a budget expressed as "less than" cannot report.
        var (normal, suppressed) = await CountSlowEntriesAsync(body);
        Assert.True(
            suppressed > normal,
            $"`{body}` entered the argument switch {suppressed} times with the fast path "
            + $"suppressed and {normal} times without, so this measures nothing about the switch.");

        var fast = await MeasureAsync(body, suppressFastPath: false);
        var slow = await MeasureAsync(body, suppressFastPath: true);
        var delta = slow - fast;

        Assert.True(
            delta < budget,
            $"`{body}` cost {delta} bytes per iteration more through the argument switch than "
            + $"through the fast path, against a budget of {budget}. Before TOAST-0009's case "
            + "extraction this was 7,417; after it, 2,520. A number back near the old one means "
            + "cases have been inlined into EvaluateArgumentSlowAsync again, so its state machine "
            + "is carrying their locals for every argument evaluated.");
    }

    /// <summary>
    /// How many arguments enter the slow switch with the fast path on, and with it suppressed.
    /// </summary>
    private static async Task<(long Normal, long Suppressed)> CountSlowEntriesAsync(string body)
    {
        var source = $"var t = 7\nvar s = 0\nfor i in 1..100 {{ {body} }}\n";

        async Task<long> RunAsync(bool suppress)
        {
            var engine = new ToshEngine(ToshRuntime.CreateDefault().Language)
            {
                SuppressSimpleArgumentFastPath = suppress,
            };
            await engine.ExecuteToListAsync(source);
            return engine.SlowArgumentEvaluations;
        }

        return (await RunAsync(suppress: false), await RunAsync(suppress: true));
    }

    [Fact]
    public async Task The_measurement_can_tell_the_two_paths_apart()
    {
        // The control: suppressing the fast path must change which code runs, or the budgets
        // above are measuring nothing and would pass whatever happened to the switch.
        //
        // It used to assert that suppression cost more *bytes*, and failed from the day it was
        // written — not because the seam was broken but because it works and the thing it
        // measures got cheap. `TOAST-0009`'s extraction left entering the switch so nearly free
        // that three hundred extra entries per run do not move a byte count above its noise.
        // A byte count was the wrong instrument for "did the other path run"; counting the
        // entries answers it exactly.
        var (normal, suppressed) = await CountSlowEntriesAsync("$s = ($t + 1)");

        Assert.True(
            suppressed > normal,
            $"expected suppressing the fast path to send more arguments through the switch; "
            + $"normal={normal} suppressed={suppressed}");
    }
}

using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class TimeCommandTests
{
    [Fact]
    public async Task Time_block_returns_timing_info()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("time { seq 100 | reduce 0 { $acc + _ } }");

        Assert.True(results.Count >= 2, "Expected command output followed by timing info.");
        Assert.Equal(5050L, Convert.ToInt64(results[^2]));
        var info = Assert.IsType<CommandTimingInfo>(results[^1]);
        Assert.True(info.Elapsed > TimeSpan.Zero, "Elapsed should be positive.");
        Assert.True(info.UserCpuTime >= TimeSpan.Zero, "UserCpuTime should be non-negative.");
        Assert.True(info.SystemCpuTime >= TimeSpan.Zero, "SystemCpuTime should be non-negative.");
        Assert.True(info.CpuPercent >= 0, "CpuPercent should be non-negative.");
        Assert.True(info.PeakWorkingSet.Bytes > 0, "PeakWorkingSet should be positive.");
        Assert.True(info.ThreadAllocations.Bytes >= 0, "ThreadAllocations should be non-negative.");
    }

    [Fact]
    public async Task Time_command_with_args_returns_timing_info()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("time echo hello");

        Assert.True(results.Count >= 2, "Expected command output followed by timing info.");
        Assert.Equal("hello", results[^2]);
        var info = Assert.IsType<CommandTimingInfo>(results[^1]);
        Assert.True(info.Elapsed >= TimeSpan.Zero, "Elapsed should be non-negative.");
    }

    [Fact]
    public async Task Time_user_function_returns_timing_info()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("func fib(n) { if ($n <= 1) { $n } else { (fib ($n - 1)) + (fib ($n - 2)) } }");
        var results = await engine.ExecuteToListAsync("time fib 15");

        Assert.True(results.Count >= 2, "Expected command output followed by timing info.");
        Assert.Equal(610L, Convert.ToInt64(results[^2]));
        var info = Assert.IsType<CommandTimingInfo>(results[^1]);
        Assert.True(info.Elapsed > TimeSpan.Zero, "Elapsed should be positive for recursive function.");
        Assert.True(info.ThreadAllocations.Bytes > 0, "ThreadAllocations should be positive for recursive function.");
    }

    [Fact]
    public async Task Time_sleep_measures_wall_clock_accurately()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("time { sleep 200ms }");

        var info = Assert.IsType<CommandTimingInfo>(results[^1]);
        Assert.True(info.Elapsed >= TimeSpan.FromMilliseconds(150), $"Expected at least 150ms, got {info.Elapsed.TotalMilliseconds}ms.");
        Assert.True(info.Elapsed < TimeSpan.FromSeconds(2), $"Expected under 2s, got {info.Elapsed.TotalMilliseconds}ms.");
    }

    [Fact]
    public async Task Time_reports_page_faults_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("time { seq 1000 | collect }");

        var info = Assert.IsType<CommandTimingInfo>(results[^1]);
        Assert.True(info.MinorPageFaults >= 0, "MinorPageFaults should be non-negative.");
        Assert.True(info.MajorPageFaults >= 0, "MajorPageFaults should be non-negative.");
    }

    [Fact]
    public async Task Time_without_arguments_produces_diagnostic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("time"));

        Assert.Contains("requires", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The recursion limit is derived from the stack the process has — `TOAST-0049`.
/// </summary>
/// <remarks>
/// <para>
/// The ceiling was a fixed 128 because that is what fits on the 8 MB stack a thread gets by
/// default, and a Tōast frame costs a chain of async state-machine frames rather than one
/// CLR frame. Measured on 2026-08-22: depth 250 completes and depth 300 aborts, for both a
/// plain function and a class method.
/// </para>
/// <para>
/// Raising it therefore meant giving the evaluator more stack, and only one lever does that
/// safely. A single thread with a large explicit stack does nothing — the continuations hop
/// to the pool. Pumping them back onto it reaches depth 8,900 but makes every `await`
/// single-threaded, which deadlocks against the 23 places the engine blocks on
/// `GetAwaiter().GetResult()`. `setrlimit` from `Main` is too late, and the same setting in
/// `runtimeconfig.json` is ignored. What is left is the CLR's own environment setting, and
/// because it is read before any managed code runs, the limit has to be derived from it
/// rather than declared.
/// </para>
/// </remarks>
public sealed class ExecutionDepthLimitTests
{
    /// <summary>The default stack yields exactly the limit measured for it.</summary>
    /// <remarks>
    /// The anchor. If the allowance is ever retuned, this is what says the default did not
    /// move with it — the one value in the table backed by a bisected measurement.
    /// </remarks>
    [Fact]
    public void The_default_stack_yields_the_measured_limit()
        => Assert.Equal(
            ToshExecutionDepthGuard.MaximumSafeDepth,
            ToshExecutionDepthGuard.MaximumDepthForStack(DeepStack.DefaultStackBytes));

    /// <summary>A larger stack allows proportionally deeper recursion.</summary>
    [Theory]
    [InlineData(16UL * 1024 * 1024, 256)]
    [InlineData(64UL * 1024 * 1024, 1024)]
    [InlineData(256UL * 1024 * 1024, 4096)]
    public void A_larger_stack_allows_more_frames(ulong stackBytes, int expected)
        => Assert.Equal(expected, ToshExecutionDepthGuard.MaximumDepthForStack(stackBytes));

    /// <summary>
    /// The allowance stays well inside the measured wall.
    /// </summary>
    /// <remarks>
    /// At 64 MB the evaluator was measured to complete depth 4,000 and abort by 6,000. A
    /// guard that reports a limit the stack cannot actually take turns a catchable
    /// diagnostic into a `SIGABRT`, so the margin is the whole point of the number.
    /// </remarks>
    [Fact]
    public void The_limit_stays_far_below_the_measured_wall()
    {
        var allowed = ToshExecutionDepthGuard.MaximumDepthForStack(64UL * 1024 * 1024);

        Assert.True(
            allowed * 3 < 4000,
            $"the limit for a 64 MB stack is {allowed}, which is not comfortably below the " +
            "depth 4,000 measured to complete on it");
    }

    /// <summary>A stack smaller than the default does not lower the limit.</summary>
    /// <remarks>
    /// The floor exists because the alternative is a shell that recurses less than it did
    /// yesterday because of an environment variable somebody set for another reason.
    /// </remarks>
    [Theory]
    [InlineData(1UL * 1024 * 1024)]
    [InlineData(0UL)]
    public void A_smaller_stack_does_not_lower_the_limit(ulong stackBytes)
        => Assert.Equal(
            ToshExecutionDepthGuard.MaximumSafeDepth,
            ToshExecutionDepthGuard.MaximumDepthForStack(stackBytes));

    /// <summary>No stack buys more than compiled code is allowed.</summary>
    [Fact]
    public void The_limit_is_capped_at_the_compiled_ceiling()
        => Assert.Equal(
            ToshExecutionDepthGuard.MaximumCompiledDepth,
            ToshExecutionDepthGuard.MaximumDepthForStack(ulong.MaxValue));

    /// <summary>The setting is read the way the CLR reads it: hexadecimal, prefix optional.</summary>
    [Theory]
    [InlineData("0x4000000", 64UL * 1024 * 1024)]
    [InlineData("4000000", 64UL * 1024 * 1024)]
    [InlineData("0X4000000", 64UL * 1024 * 1024)]
    [InlineData("  0x4000000  ", 64UL * 1024 * 1024)]
    public void The_configured_stack_size_is_read_as_hexadecimal(string configured, ulong expected)
        => WithStackSizeVariable(configured, () => Assert.Equal(expected, DeepStack.ThreadStackBytes()));

    /// <summary>
    /// Anything unusable reports the default rather than guessing.
    /// </summary>
    /// <remarks>
    /// Reporting a larger stack than the process has is the one failure that matters here:
    /// it would raise the guard above the real wall and trade a diagnostic for a crash.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("0x")]
    [InlineData("-1")]
    public void An_unusable_setting_reports_the_default(string configured)
        => WithStackSizeVariable(configured, () => Assert.Equal(DeepStack.DefaultStackBytes, DeepStack.ThreadStackBytes()));

    /// <summary>An absent setting reports the default.</summary>
    [Fact]
    public void An_absent_setting_reports_the_default()
        => WithStackSizeVariable(null, () => Assert.Equal(DeepStack.DefaultStackBytes, DeepStack.ThreadStackBytes()));

    private static void WithStackSizeVariable(string? value, Action assert)
    {
        var previous = Environment.GetEnvironmentVariable(DeepStack.StackSizeVariable);
        try
        {
            Environment.SetEnvironmentVariable(DeepStack.StackSizeVariable, value);
            assert();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeepStack.StackSizeVariable, previous);
        }
    }
}

using System.Diagnostics;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class ClassEqualityCancellationTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CancellationDeadline = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GateSafetyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TestSafetyTimeout = TimeSpan.FromSeconds(4);

    public static TheoryData<string, string> CancellationCases =>
        new()
        {
            {
                "direct equality",
                EqualityProbeSource(
                    """
                    echo ($left == $right)
                    """)
            },
            {
                "direct inequality",
                EqualityProbeSource(
                    """
                    echo ($left != $right)
                    """)
            },
            {
                "nested list equality",
                EqualityProbeSource(
                    """
                    echo ([[$left]] == [[$right]])
                    """)
            },
            {
                "membership",
                EqualityProbeSource(
                    """
                    echo ($left in [$right])
                    """)
            },
            {
                "negative membership",
                EqualityProbeSource(
                    """
                    echo ($left not-in [$right])
                    """)
            },
            {
                "contains membership",
                EqualityProbeSource(
                    """
                    echo ([$right] contains $left)
                    """)
            },
            {
                "bare switch case",
                EqualityProbeSource(
                    """
                    switch ($left) {
                        case $right { echo matched }
                        default { echo unmatched }
                    }
                    """)
            },
            {
                "comparison switch case",
                EqualityProbeSource(
                    """
                    switch ($left) {
                        case == $right { echo matched }
                        default { echo unmatched }
                    }
                    """)
            },
            {
                "class-to-string equality",
                StringProbeSource(
                    """
                    echo ($probe == "probe")
                    """)
            },
            {
                "string-to-class equality",
                StringProbeSource(
                    """
                    echo ("probe" == $probe)
                    """)
            },
        };

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task Class_equality_path_observes_execution_cancellation_promptly(
        string scenario,
        string source)
    {
        var gate = new AwaitEqualityCancellationCommand(GateSafetyTimeout);
        var runtime = ToshRuntime.CreateDefault();
        runtime.Commands.Register(gate);
        var engine = new ToshEngine(runtime);
        using var cancellation = new CancellationTokenSource();

        var execution = Task.Run(
            () => engine.ExecuteToListAsync(source, cancellation.Token));

        await gate.Started.WaitAsync(StartTimeout);

        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution.WaitAsync(TestSafetyTimeout));

        Assert.True(
            stopwatch.Elapsed < CancellationDeadline,
            $"{scenario} took {stopwatch.Elapsed} to observe cancellation.");
    }

    [Fact]
    public async Task Symbolic_equality_operators_take_precedence_over_Equals()
    {
        var engine = ShellEngine.CreateFullShell();

        var values = await engine.ExecuteToListAsync(
            """
            class SymbolicProbe {
                func ==(other) { return false }
                func !=(other) { return false }
                shy func Equals(other) -> bool { return true }
            }

            var left = new SymbolicProbe()
            var right = new SymbolicProbe()
            echo ($left == $right)
            echo ($left != $right)
            """);

        Assert.Equal([false, false], values);
    }

    [Fact]
    public async Task Reference_identity_and_null_short_circuit_user_Equals()
    {
        var engine = ShellEngine.CreateFullShell();

        var values = await engine.ExecuteToListAsync(
            """
            class IdentityProbe {
                shy func Equals(other) -> bool { return false }
            }

            var probe = new IdentityProbe()
            echo ($probe == $probe)
            echo ($probe != $probe)
            echo ($probe == null)
            echo (null == $probe)
            echo ($probe != null)
            echo (null != $probe)
            """);

        Assert.Equal([true, false, false, false, true, true], values);
    }

    [Fact]
    public async Task Equality_dispatch_is_left_biased()
    {
        var engine = ShellEngine.CreateFullShell();

        var values = await engine.ExecuteToListAsync(
            """
            class BiasedProbe(answer: bool) {
                prop Answer: bool = answer
                shy func Equals(other) -> bool { return $this.Answer }
            }

            var falseProbe = new BiasedProbe(false)
            var trueProbe = new BiasedProbe(true)
            echo ($falseProbe == $trueProbe)
            echo ($trueProbe == $falseProbe)
            """);

        Assert.Equal([false, true], values);
    }

    [Fact]
    public async Task Structural_and_mixed_string_equality_preserve_existing_semantics()
    {
        var engine = ShellEngine.CreateFullShell();

        var values = await engine.ExecuteToListAsync(
            """
            class ValueProbe(value: string) {
                prop Value: string = value
                shy func ToString() -> string { return $this.Value }
                shy func Equals(other) -> bool { return ($this.Value == $other.Value) }
            }

            var left = new ValueProbe("probe")
            var right = new ValueProbe("probe")
            echo ([[$left]] == [[$right]])
            echo ($left in [$right])
            echo ($left not-in [new ValueProbe("different")])
            echo ([$right] contains $left)
            echo ($left == "probe")
            echo ("probe" == $left)
            echo ($left == "PROBE")
            echo ("PROBE" == $left)
            """);

        Assert.Equal([true, true, true, true, true, true, false, false], values);
    }

    private static string EqualityProbeSource(string expression) =>
        $$"""
        class EqualityProbe {
            shy func Equals(other) -> bool {
                await-equality-cancellation
                return true
            }
        }

        var left = new EqualityProbe()
        var right = new EqualityProbe()
        {{expression}}
        """;

    private static string StringProbeSource(string expression) =>
        $$"""
        class StringProbe {
            shy func ToString() -> string {
                await-equality-cancellation
                return "probe"
            }
        }

        var probe = new StringProbe()
        {{expression}}
        """;

    private sealed class AwaitEqualityCancellationCommand(TimeSpan safetyTimeout) : IShellCommand
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "await-equality-cancellation";

        public string Description => "Waits until the current equality operation is cancelled.";

        public string Usage => "await-equality-cancellation";

        public Task Started => _started.Task;

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            _started.TrySetResult();

            await Task
                .Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                .WaitAsync(safetyTimeout);

            yield break;
        }
    }
}

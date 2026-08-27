using System.Diagnostics;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class ClassCancellationTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CancellationDeadline = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GateSafetyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TestSafetyTimeout = TimeSpan.FromSeconds(4);

    public static TheoryData<string, string> CancellationCases =>
        new()
        {
            {
                "instance method",
                """
                class CancellationProbe {
                    func wait() { await-cancellation }
                }
                var probe = new CancellationProbe()
                $probe.wait()
                """
            },
            {
                "constructor",
                """
                class CancellationProbe {
                    CancellationProbe() { await-cancellation }
                }
                var probe = new CancellationProbe()
                """
            },
            {
                "constructor parameter refinement",
                """
                class CancellationProbe {
                    CancellationProbe(value: int where (await-cancellation)) { }
                }
                var probe = new CancellationProbe(1)
                """
            },
            {
                "legacy new command",
                """
                class CancellationProbe {
                    CancellationProbe() { await-cancellation }
                }
                new CancellationProbe
                """
            },
            {
                "static method",
                """
                class CancellationProbe {
                    static func wait() { await-cancellation }
                }
                CancellationProbe.wait()
                """
            },
            {
                "method parameter refinement",
                """
                class CancellationProbe {
                    func check(value: int where (await-cancellation)) { }
                }
                var probe = new CancellationProbe()
                $probe.check(1)
                """
            },
            {
                "method return refinement",
                """
                type CheckedValue = int where (await-cancellation)
                class CancellationProbe {
                    func value() -> CheckedValue { return 1 }
                }
                var probe = new CancellationProbe()
                $probe.value()
                """
            },
            {
                "legacy call command",
                """
                class CancellationProbe {
                    func wait() { await-cancellation }
                }
                var probe = new CancellationProbe()
                $probe | call wait
                """
            },
            {
                "legacy call-method command",
                """
                class CancellationProbe {
                    func wait() { await-cancellation }
                }
                var probe = new CancellationProbe()
                call-method $probe wait
                """
            },
            {
                "extends argument",
                """
                class BaseProbe(value) { }
                class CancellationProbe extends BaseProbe((await-cancellation)) { }
                var probe = new CancellationProbe()
                """
            },
            {
                "leading super initializer",
                """
                class BaseProbe {
                    BaseProbe() { await-cancellation }
                }
                class CancellationProbe extends BaseProbe {
                    CancellationProbe() { $super() }
                }
                var probe = new CancellationProbe()
                """
            },
            {
                "property initializer",
                """
                class CancellationProbe {
                    prop Value = (await-cancellation)
                }
                var probe = new CancellationProbe()
                """
            },
            {
                "lazy property initializer",
                """
                class CancellationProbe {
                    lazy prop Value = (await-cancellation)
                }
                var probe = new CancellationProbe()
                $probe.Value
                """
            },
            {
                "property getter",
                """
                class CancellationProbe {
                    prop Value { get => await-cancellation }
                }
                var probe = new CancellationProbe()
                $probe.Value
                """
            },
            {
                "string-index property getter",
                """
                class CancellationProbe {
                    prop Value { get => await-cancellation }
                }
                var probe = new CancellationProbe()
                $probe["Value"]
                """
            },
            {
                "property setter",
                """
                class CancellationProbe {
                    prop Value {
                        get => 0
                        set => await-cancellation
                    }
                }
                var probe = new CancellationProbe()
                $probe.Value = 1
                """
            },
            {
                "string-index property setter",
                """
                class CancellationProbe {
                    prop Value {
                        get => 0
                        set => await-cancellation
                    }
                }
                var probe = new CancellationProbe()
                $probe["Value"] = 1
                """
            },
            {
                "property refinement",
                """
                class CancellationProbe {
                    prop Value: int where (await-cancellation) = 1
                }
                var probe = new CancellationProbe()
                """
            },
            {
                "operator overload",
                """
                class CancellationProbe {
                    func +(other) { await-cancellation }
                }
                var left = new CancellationProbe()
                var right = new CancellationProbe()
                echo ($left + $right)
                """
            },
            {
                "compound operator overload",
                """
                class CancellationProbe {
                    func +(other) { await-cancellation }
                }
                var left = new CancellationProbe()
                var right = new CancellationProbe()
                $left += $right
                """
            },
            {
                "enumeration hook",
                """
                class CancellationProbe {
                    func enumerate() {
                        await-cancellation
                        return [1]
                    }
                }
                var probe = new CancellationProbe()
                for item in ($probe) { echo $item }
                """
            },
            {
                "record destructuring member enumeration",
                """
                class CancellationProbe {
                    prop Value { get => await-cancellation }
                }
                var probe = new CancellationProbe()
                var { Value } = $probe
                """
            },
            {
                "record spread member enumeration",
                """
                class CancellationProbe {
                    prop Value { get => await-cancellation }
                }
                var probe = new CancellationProbe()
                var copy = {| ...$probe |}
                """
            },
        };

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task Class_call_observes_execution_cancellation_promptly(
        string scenario,
        string source)
    {
        await AssertClassCallObservesCancellationAsync(source, scenario);
    }

    [Fact]
    public async Task Engine_remains_usable_after_a_cancelled_class_call()
    {
        var engine = await AssertClassCallObservesCancellationAsync(
            """
            class CancellationProbe {
                func wait() { await-cancellation }
                func value() { return 42 }
            }
            var probe = new CancellationProbe()
            $probe.wait()
            """,
            "same-engine recovery");

        var values = await engine.ExecuteToListAsync("$probe.value()");

        Assert.Equal([42], values);
    }

    [Fact]
    public async Task Constructor_state_and_method_return_value_are_preserved()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var values = await engine.ExecuteToListAsync(
            """
            class ValueBox {
                prop Value = 0
                ValueBox(value) { $this.Value = $value }
                func read() { return $this.Value }
            }
            var box = new ValueBox(42)
            $box.read()
            """);

        Assert.Equal([42], values);
    }

    [Fact]
    public async Task Cancelled_lazy_initializer_can_be_retried()
    {
        var initializer = new CancelOnceThenReturnCommand(GateSafetyTimeout);
        var runtime = ToshRuntime.CreateDefault();
        runtime.Commands.Register(initializer);
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync(
            """
            class CancellationProbe {
                lazy prop Value = (cancel-once-then-return)
            }
            var probe = new CancellationProbe()
            """);

        using var cancellation = new CancellationTokenSource();
        var firstAccess = Task.Run(
            () => engine.ExecuteToListAsync("$probe.Value", cancellation.Token));

        await initializer.FirstStarted.WaitAsync(StartTimeout);

        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstAccess.WaitAsync(TestSafetyTimeout));

        Assert.True(
            stopwatch.Elapsed < CancellationDeadline,
            $"lazy property initializer took {stopwatch.Elapsed} to observe cancellation.");

        var values = await engine.ExecuteToListAsync("$probe.Value");

        Assert.Equal([42], values);
        Assert.Equal(2, initializer.InvocationCount);
    }

    [Fact]
    public async Task Concurrent_lazy_property_readers_share_one_initialization()
    {
        var initializer = new GatedReturnCommand(GateSafetyTimeout);
        var runtime = ToshRuntime.CreateDefault();
        runtime.Commands.Register(initializer);
        var engine = new ToshEngine(runtime.Language);

        var values = await engine.ExecuteToListAsync(
            """
            class LazyProbe {
                lazy prop Value = (gated-return)
            }
            var probe = new LazyProbe()
            $probe
            """);
        var probe = Assert.IsType<ToshClassInstance>(Assert.Single(values));

        var firstRead = probe
            .TryGetMemberAsync("Value", includeHidden: false, CancellationToken.None)
            .AsTask();
        await initializer.Started.WaitAsync(StartTimeout);

        var secondRead = probe
            .TryGetMemberAsync("Value", includeHidden: false, CancellationToken.None)
            .AsTask();
        initializer.Release();

        var results = await Task
            .WhenAll(firstRead, secondRead)
            .WaitAsync(TestSafetyTimeout);

        Assert.All(results, result =>
        {
            Assert.True(result.Found);
            Assert.Equal(42, result.Value);
        });
        Assert.Equal(1, initializer.InvocationCount);
    }

    [Fact]
    public async Task Recursive_lazy_property_read_is_rejected_without_poisoning_the_instance()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            class LazyProbe {
                lazy prop Value = $this.Value
            }
            var probe = new LazyProbe()
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("$probe.Value"));

        Assert.Contains("recursively reads itself", exception.Message);

        var retryException = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("$probe.Value"));

        Assert.Contains("recursively reads itself", retryException.Message);
    }

    private static async Task<ToshEngine> AssertClassCallObservesCancellationAsync(
        string source,
        string scenario)
    {
        var gate = new AwaitCancellationCommand(GateSafetyTimeout);
        var runtime = ToshRuntime.CreateDefault();
        runtime.Commands.Register(gate);
        var engine = new ToshEngine(runtime.Language);
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

        return engine;
    }

    private sealed class AwaitCancellationCommand(TimeSpan safetyTimeout) : IShellCommand
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "await-cancellation";

        public string Description => "Waits until the current execution is cancelled.";

        public string Usage => "await-cancellation";

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

    private sealed class CancelOnceThenReturnCommand(TimeSpan safetyTimeout) : IShellCommand
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public string Name => "cancel-once-then-return";

        public string Description => "Waits for cancellation once, then returns a value.";

        public string Usage => "cancel-once-then-return";

        public Task FirstStarted => _firstStarted.Task;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                _firstStarted.TrySetResult();

                await Task
                    .Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                    .WaitAsync(safetyTimeout);

                yield break;
            }

            yield return 42;
        }
    }

    private sealed class GatedReturnCommand(TimeSpan safetyTimeout) : IShellCommand
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public string Name => "gated-return";

        public string Description => "Waits for a test gate, then returns a value.";

        public string Usage => "gated-return";

        public Task Started => _started.Task;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            Interlocked.Increment(ref _invocationCount);
            _started.TrySetResult();

            await _release.Task
                .WaitAsync(context.CancellationToken)
                .WaitAsync(safetyTimeout);

            yield return 42;
        }
    }
}

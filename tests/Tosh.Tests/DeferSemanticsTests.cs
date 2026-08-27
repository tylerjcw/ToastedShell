using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class DeferSemanticsTests
{
    [Fact]
    public async Task Body_and_cleanup_failures_are_preserved_after_exhaustive_lifo_cleanup()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            var trace = ""
            var caught = null
            func fail() {
                defer { $trace = $trace + "A"; throw "cleanup-A" }
                defer { $trace = $trace + "B"; throw "cleanup-B" }
                throw "body"
            }
            try { fail } catch (err) { $caught = $err }
            """);

        Assert.True(engine.TryGetVariableValue("trace", out var trace));
        Assert.Equal("BA", trace);

        Assert.True(engine.TryGetVariableValue("caught", out var caught));
        var aggregate = Assert.IsType<ToshDeferAggregateException>(caught);
        Assert.Equal("body", PayloadOf(aggregate.BodyFailure));
        Assert.Equal(
            ["cleanup-B", "cleanup-A"],
            aggregate.CleanupFailures.Select(PayloadOf).ToArray());
        Assert.Equal(
            ["body", "cleanup-B", "cleanup-A"],
            aggregate.Failures.Select(PayloadOf).ToArray());
    }

    [Fact]
    public async Task Cleanup_only_failures_remain_cleanup_failures_in_lifo_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            var trace = ""
            var caught = null
            func fail-cleanup() {
                defer { $trace = $trace + "A"; throw "cleanup-A" }
                defer { $trace = $trace + "B"; throw "cleanup-B" }
            }
            try { fail-cleanup } catch (err) { $caught = $err }
            """);

        Assert.True(engine.TryGetVariableValue("trace", out var trace));
        Assert.Equal("BA", trace);

        Assert.True(engine.TryGetVariableValue("caught", out var caught));
        var aggregate = Assert.IsType<ToshDeferAggregateException>(caught);
        Assert.Null(aggregate.BodyFailure);
        Assert.Equal(
            ["cleanup-B", "cleanup-A"],
            aggregate.CleanupFailures.Select(PayloadOf).ToArray());
    }

    [Fact]
    public async Task Unreached_defer_is_not_registered()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            var trace = ""
            var caught = null
            func stop-registering() {
                defer { $trace = $trace + "A"; throw "cleanup-A" }
                throw "body"
                defer { $trace = $trace + "X"; throw "unreached" }
            }
            try { stop-registering } catch (err) { $caught = $err }
            """);

        Assert.True(engine.TryGetVariableValue("trace", out var trace));
        Assert.Equal("A", trace);

        Assert.True(engine.TryGetVariableValue("caught", out var caught));
        var aggregate = Assert.IsType<ToshDeferAggregateException>(caught);
        Assert.Equal("body", PayloadOf(aggregate.BodyFailure));
        Assert.Equal(["cleanup-A"], aggregate.CleanupFailures.Select(PayloadOf).ToArray());
    }

    [Fact]
    public async Task Defer_preserves_output_emitted_before_return()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            func output-then-return() {
                defer { var cleaned = true }
                echo before
                return "after"
            }
            """);

        var values = await engine.ExecuteToListAsync("output-then-return");

        Assert.Equal(
            ["before", "after"],
            values.Select(value => value?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Cleanup_control_flow_is_suppressed_without_replacing_the_pending_return()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            func return-through-cleanup() {
                defer { return "cleanup-value" }
                return "body-value"
            }
            """);

        var values = await engine.ExecuteToListAsync("return-through-cleanup");

        Assert.Equal(
            ["body-value"],
            values.Select(value => value?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Cleanup_failures_supersede_pending_return_break_and_continue()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            var trace = ""
            var returnFailure = null
            var breakFailure = null
            var continueFailure = null

            func fail-return() {
                defer { $trace = $trace + "R"; throw "cleanup-return" }
                return 1
            }
            func fail-break() {
                for i in (1..3) {
                    defer { $trace = $trace + "B"; throw "cleanup-break" }
                    break
                }
                $trace = $trace + "X"
            }
            func fail-continue() {
                for i in (1..3) {
                    defer { $trace = $trace + "C"; throw "cleanup-continue" }
                    continue
                }
                $trace = $trace + "Y"
            }

            try { fail-return } catch (err) { $returnFailure = $err }
            try { fail-break } catch (err) { $breakFailure = $err }
            try { fail-continue } catch (err) { $continueFailure = $err }
            """);

        Assert.True(engine.TryGetVariableValue("trace", out var trace));
        Assert.Equal("RBC", trace);

        Assert.True(engine.TryGetVariableValue("returnFailure", out var returnFailure));
        Assert.True(engine.TryGetVariableValue("breakFailure", out var breakFailure));
        Assert.True(engine.TryGetVariableValue("continueFailure", out var continueFailure));
        Assert.Equal("cleanup-return", returnFailure);
        Assert.Equal("cleanup-break", breakFailure);
        Assert.Equal("cleanup-continue", continueFailure);
    }

    [Fact]
    public async Task Cleanup_local_break_and_continue_are_suppressed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            var trace = ""
            func cleanup-jumps() {
                defer {
                    $trace = $trace + "A"
                    break
                    $trace = $trace + "X"
                }
                defer {
                    $trace = $trace + "B"
                    continue
                    $trace = $trace + "Y"
                }
                return 7
            }
            """);

        var values = await engine.ExecuteToListAsync("cleanup-jumps");

        Assert.True(engine.TryGetVariableValue("trace", out var trace));
        Assert.Equal("BA", trace);
        Assert.Equal(
            ["7"],
            values.Select(value => value?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Nested_defer_failures_flatten_in_execution_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            var caught = null
            func nested-failures() {
                defer { throw "oldest-cleanup" }
                defer {
                    defer { throw "nested-cleanup" }
                    throw "nested-body"
                }
                throw "outer-body"
            }
            try { nested-failures } catch (err) { $caught = $err }
            """);

        Assert.True(engine.TryGetVariableValue("caught", out var caught));
        var aggregate = Assert.IsType<ToshDeferAggregateException>(caught);
        Assert.Equal("outer-body", PayloadOf(aggregate.BodyFailure));
        Assert.Equal(
            ["nested-body", "nested-cleanup", "oldest-cleanup"],
            aggregate.CleanupFailures.Select(PayloadOf).ToArray());
    }

    [Fact]
    public async Task Unhandled_cleanup_failure_uses_the_stable_cleanup_diagnostic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            func cleanup-fails() {
                defer { throw "cleanup" }
            }
            """);

        var failure = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("cleanup-fails"));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("tosh.runtime.defer_cleanup_failed", diagnostic.Code);
        Assert.Contains("cleanup", diagnostic.Title, StringComparison.Ordinal);
        Assert.Equal("<input>", diagnostic.SourceName);
        Assert.Contains(
            "throw \"cleanup\"",
            diagnostic.SourceText ?? string.Empty,
            StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Span);
    }

    [Fact]
    public async Task Unhandled_competing_failures_render_in_body_then_cleanup_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            func everything-fails() {
                defer { throw "cleanup-A" }
                defer { throw "cleanup-B" }
                throw "body"
            }
            """);

        var failure = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("everything-fails"));

        Assert.Equal(
            [
                "tosh.runtime.defer_body_failed",
                "tosh.runtime.defer_cleanup_failed",
                "tosh.runtime.defer_cleanup_failed",
            ],
            failure.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray());
        Assert.Contains("body", failure.Diagnostics[0].Title, StringComparison.Ordinal);
        Assert.Contains("cleanup-B", failure.Diagnostics[1].Title, StringComparison.Ordinal);
        Assert.Contains("cleanup-A", failure.Diagnostics[2].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_runs_reached_cleanup_with_a_shielded_token()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = ToshRuntime.CreateDefault();
        runtime.Commands.Register(new CancelNowCommand(cancellation));
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync(
            """
            var cleanupTrace = ""
            func cancel-with-cleanup() {
                defer { $cleanupTrace = $cleanupTrace + "cleaned" }
                cancel-now
            }
            """);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.ExecuteToListAsync("cancel-with-cleanup", cancellation.Token));

        Assert.True(engine.TryGetVariableValue("cleanupTrace", out var cleanupTrace));
        Assert.Equal("cleaned", cleanupTrace);
    }

    [Fact]
    public async Task Cancellation_stays_cancellation_and_retains_cleanup_failures()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = ToshRuntime.CreateDefault();
        runtime.Commands.Register(new CancelNowCommand(cancellation));
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync(
            """
            var cleanupTrace = ""
            func cancel-and-fail-cleanup() {
                defer { $cleanupTrace = $cleanupTrace + "A"; throw "cleanup" }
                defer { $cleanupTrace = $cleanupTrace + "B" }
                cancel-now
            }
            """);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.ExecuteToListAsync(
                "cancel-and-fail-cleanup",
                cancellation.Token));

        Assert.True(engine.TryGetVariableValue("cleanupTrace", out var cleanupTrace));
        Assert.Equal("BA", cleanupTrace);
        var cleanupFailure = Assert.Single(
            ToshDeferFailures.GetCleanupFailures(failure));
        Assert.Equal("cleanup", PayloadOf(cleanupFailure));
    }

    private static string PayloadOf(Exception? exception)
        => exception switch
        {
            ThrowSignalException signal => signal.Value?.ToString() ?? string.Empty,
            null => string.Empty,
            _ => exception.Message,
        };

    private sealed class CancelNowCommand(CancellationTokenSource cancellation) : IShellCommand
    {
        public string Name => "cancel-now";

        public string Description => "Cancels the test execution token.";

        public string Usage => "cancel-now";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            cancellation.Cancel();
            await Task.Yield();
            context.CancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}

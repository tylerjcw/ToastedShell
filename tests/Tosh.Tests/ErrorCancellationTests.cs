using System.Diagnostics;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class ErrorCancellationTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CancellationDeadline = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GateSafetyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TestSafetyTimeout = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task Computed_error_message_observes_cancellation_during_throw_wrapping()
    {
        var engine = await AssertErrorPathObservesCancellationAsync(
            """
            class MessageProbeError extends Error {
                prop Message { get => await-error-cancellation }
            }
            throw (new MessageProbeError())
            """,
            "computed Error.Message");

        var recovered = await engine.ExecuteToListAsync("echo recovered");

        Assert.Equal(["recovered"], recovered);
    }

    [Theory]
    [InlineData("DiagnosticTitle")]
    [InlineData("Code")]
    [InlineData("Label")]
    public async Task Computed_error_diagnostic_member_observes_cancellation_during_uncaught_rendering(
        string diagnosticMember)
    {
        var source = $$"""
            class DiagnosticProbeError extends Error {
                prop Message = "boom"
                prop {{diagnosticMember}} { get => await-error-cancellation }
            }
            throw (new DiagnosticProbeError())
            """;

        var engine = await AssertErrorPathObservesCancellationAsync(
            source,
            $"computed Error.{diagnosticMember}");

        var recovered = await engine.ExecuteToListAsync("echo recovered");

        Assert.Equal(["recovered"], recovered);
    }

    [Fact]
    public async Task Computed_error_diagnostic_members_map_normally()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                class DiagnosticProbeError extends Error {
                    prop Message { get => "wrapper message" }
                    prop DiagnosticTitle { get => "computed title" }
                    prop Code { get => "probe.computed" }
                    prop Label { get => "computed label" }
                }
                throw (new DiagnosticProbeError())
                """));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("computed title", diagnostic.Title);
        Assert.Equal("probe.computed", diagnostic.Code);
        Assert.Equal("computed label", diagnostic.Label);
    }

    private static async Task<ToshEngine> AssertErrorPathObservesCancellationAsync(
        string source,
        string scenario)
    {
        var gate = new AwaitErrorCancellationCommand(GateSafetyTimeout);
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

    private sealed class AwaitErrorCancellationCommand(TimeSpan safetyTimeout) : IShellCommand
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "await-error-cancellation";

        public string Description => "Waits until the current error-path execution is cancelled.";

        public string Usage => "await-error-cancellation";

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

using System.Diagnostics;
using Tosh.Runtime;

namespace Tosh.Cli;

/// <summary>
/// Decides at runtime whether to stream rows or buffer the full result set.
/// Values are buffered until the gap between two successive arrivals exceeds
/// <see cref="StreamingThreshold"/>.  At that point the sink promotes to streaming:
/// buffered values are flushed to a <see cref="StreamingTableSink"/> and subsequent
/// values flow through immediately.
/// Commands that produce all output in a burst (ls, ps) stay buffered and render
/// as a single batch.  Commands that trickle output (ping, tail -f) get row-by-row
/// streaming after the first slow gap is detected.
/// Output-redirection and non-REPL contexts always use buffering.
/// </summary>
internal sealed class AutoDisplaySink : IDisplaySink
{
    private static readonly TimeSpan StreamingThreshold = TimeSpan.FromMilliseconds(250);

    private readonly ToshRuntime _runtime;
    private readonly bool _renderTuiOutcome;
    private readonly bool _canStream;
    private readonly Stopwatch _gapTimer = Stopwatch.StartNew();

    private enum Mode { Buffering, Streaming }
    private Mode _mode = Mode.Buffering;

    private readonly List<object?> _buffer = [];
    private StreamingTableSink? _streamingSink;

    public AutoDisplaySink(ToshRuntime runtime, bool renderTuiOutcome)
    {
        _runtime = runtime;
        _renderTuiOutcome = renderTuiOutcome;
        _canStream = renderTuiOutcome && !Console.IsOutputRedirected && !Console.IsErrorRedirected;
    }

    public async ValueTask EmitAsync(object? value, CancellationToken cancellationToken = default)
    {
        var gap = _gapTimer.Elapsed;
        _gapTimer.Restart();

        if (_mode == Mode.Streaming)
        {
            await _streamingSink!.EmitAsync(value, cancellationToken);
            return;
        }

        // Still buffering — check if the gap since the last value is large enough to promote.
        if (_canStream && gap >= StreamingThreshold)
        {
            _mode = Mode.Streaming;
            _streamingSink = new StreamingTableSink(_runtime);

            // Flush previously buffered values first, then the current one.
            foreach (var buffered in _buffer)
                await _streamingSink.EmitAsync(buffered, cancellationToken);
            _buffer.Clear();

            await _streamingSink.EmitAsync(value, cancellationToken);
            return;
        }

        _buffer.Add(value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mode == Mode.Streaming)
        {
            await _streamingSink!.DisposeAsync();
            return;
        }

        await using var sink = new BufferingDisplaySink(_runtime, _renderTuiOutcome);
        foreach (var v in _buffer)
            await sink.EmitAsync(v);
    }
}

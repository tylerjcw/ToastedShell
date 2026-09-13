using System.Collections;
using Tosh.Runtime;

namespace Tosh.Tui;

/// <summary>One source a widget is fed by, and what to do with what arrives.</summary>
public sealed record TuiFeed(object? Source, Action<object?> Arrived, Action? Ended = null);

/// <summary>
/// The sources a widget is fed by, pumped off the render loop.
/// </summary>
/// <remarks>
/// <para>
/// Written the same way <see cref="TuiShortcuts"/> is, and hung on a widget the same way,
/// because the two answer the same kind of question: what, other than a keystroke, makes
/// this part of the screen change. A key is an event the reader causes; a feed is one
/// something else does.
/// </para>
/// <code>
/// $screen.Feeds = new TuiFeeds()
/// $screen.Feeds.From($lines, &amp;Append) | ignore
/// </code>
/// <para>
/// A source is read on a task of its own and each value is posted to the loop, so the
/// handler runs on the loop's thread between frames. Nothing in a handler needs a lock,
/// and nothing a handler does can be half-drawn.
/// </para>
/// </remarks>
public sealed class TuiFeeds
{
    private readonly List<TuiFeed> _feeds = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _started;

    /// <summary>The sources registered, in the order they were added.</summary>
    public IReadOnlyList<TuiFeed> Sources => _feeds;

    /// <summary>Reads a source, handing each value to <paramref name="arrived"/>.</summary>
    public TuiFeeds From(object? source, Action<object?> arrived)
        => From(source, arrived, ended: null);

    /// <summary>Reads a source, and says so when it runs out.</summary>
    public TuiFeeds From(object? source, Action<object?> arrived, Action? ended)
    {
        ArgumentNullException.ThrowIfNull(arrived);

        _feeds.Add(new TuiFeed(source, arrived, ended));
        return this;
    }

    /// <summary>Starts reading every source, posting what arrives to <paramref name="wake"/>.</summary>
    internal void Start(TuiWake wake)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        foreach (var feed in _feeds)
        {
            var pump = Pump(feed, wake, _stopping.Token);

            // Deliberately not awaited and deliberately not stored: a source that ends is
            // done, and one that faults reports through the handler guard like any other
            // script failure rather than tearing the screen down.
            _ = pump;
        }
    }

    /// <summary>Stops reading, when the screen holding this has ended.</summary>
    internal void Stop()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }
    }

    private static async Task Pump(TuiFeed feed, TuiWake wake, CancellationToken cancellation)
    {
        try
        {
            switch (feed.Source)
            {
                case ShellChannel channel:
                    await PumpChannel(channel, feed, wake, cancellation).ConfigureAwait(false);
                    break;

                case IAsyncEnumerable<object?> sequence:
                    await foreach (var value in sequence.WithCancellation(cancellation).ConfigureAwait(false))
                    {
                        wake.Post(() => feed.Arrived(value));
                    }

                    break;

                case Task task:
                    await task.ConfigureAwait(false);
                    wake.Post(() => feed.Arrived(Result(task)));
                    break;

                // A list, a range, a generator: read once, in order, off the loop. Useful
                // for a source that is slow to produce rather than slow to arrive.
                case IEnumerable sequence and not string:
                    foreach (var value in sequence)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        wake.Post(() => feed.Arrived(value));
                    }

                    break;

                case null:
                    break;

                default:
                    wake.Post(() => feed.Arrived(feed.Source));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The screen ended first. Not a failure.
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                              and not StackOverflowException)
        {
            // Reported where every other handler failure is: on the loop's thread, by the
            // guard, so it reaches the banner rather than a background task nobody awaits.
            wake.Post(() => throw new TuiFeedException(exception));
            return;
        }

        if (feed.Ended is { } ended)
        {
            wake.Post(ended);
        }
    }

    private static async Task PumpChannel(
        ShellChannel channel,
        TuiFeed feed,
        TuiWake wake,
        CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var received = await channel.ReceiveResultAsync(cancellation).ConfigureAwait(false);

            if (!received.HasValue)
            {
                // Closed and drained. `HasValue` is what tells that apart from a value
                // that is genuinely null, which a channel is allowed to carry.
                return;
            }

            var value = received.Value;

            wake.Post(() => feed.Arrived(value));
        }
    }

    /// <summary>What a task produced, for the generic ones that produce anything.</summary>
    private static object? Result(Task task)
        => task.GetType().GetProperty("Result")?.GetValue(task);
}

/// <summary>A source failed, reported on the loop's thread rather than on the pump's.</summary>
public sealed class TuiFeedException(Exception cause)
    : Exception($"A screen's source failed: {cause.Message}", cause);

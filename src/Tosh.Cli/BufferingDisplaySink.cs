using Tosh.Cli.Tui;
using Tosh.Runtime;

namespace Tosh.Cli;

/// <summary>
/// Collects all emitted values and renders them as a single batch on dispose — exactly
/// the behaviour that existed before the IDisplaySink abstraction was introduced.
/// Zero behaviour change; used as the safe default until the streaming path is ready.
/// </summary>
internal sealed class BufferingDisplaySink : IDisplaySink
{
    private readonly ToshRuntime _runtime;
    private readonly bool _renderTuiOutcome;
    private readonly List<object?> _values = [];

    /// <param name="runtime">The active runtime.</param>
    /// <param name="renderTuiOutcome">
    /// True in REPL mode: if a TUI request is handled, any outcome values are printed.
    /// False in script/command mode: TUI requests are handled silently (no printed outcome).
    /// </param>
    public BufferingDisplaySink(ToshRuntime runtime, bool renderTuiOutcome)
    {
        _runtime = runtime;
        _renderTuiOutcome = renderTuiOutcome;
    }

    public ValueTask EmitAsync(object? value, CancellationToken cancellationToken = default)
    {
        _values.Add(value);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (TuiRequestProbe.IsTuiRequestBatch(_values) &&
                TuiRequestDispatcher.TryHandle(_values, _runtime, out var outcomeValues))
            {
                if (_renderTuiOutcome && outcomeValues is { Count: > 0 })
                {
                    var rendered = _runtime.Display.RenderMany(
                        outcomeValues, ConsoleDisplay.CreateRenderOptions(_runtime));
                    await ConsoleDisplay.WriteRenderedAsync(rendered, _runtime);
                }

                return;
            }

            var rendered2 = _runtime.Display.RenderMany(
                _values, ConsoleDisplay.CreateRenderOptions(_runtime));
            await ConsoleDisplay.WriteRenderedAsync(rendered2, _runtime);
        }
        finally
        {
            _runtime.ClearDisplaySelections();
        }
    }
}

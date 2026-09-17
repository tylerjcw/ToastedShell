using Tosh.Runtime;

namespace Tosh.Tui.Requests;

/// <summary>Request yielded by <c>tui run</c> when it is given a record tree.</summary>
/// <param name="Node">The record describing the screen.</param>
/// <param name="ReturnOutcome">Whether to yield the outcome object rather than the values.</param>
/// <param name="Invoke">
/// Calls a script function with one argument. Built by <c>tui run</c> rather than by the
/// TUI runtime, which has no command context and should not grow one in order to call
/// back into the shell.
/// </param>
/// <param name="RefreshInterval">How often to redraw with no input, if at all.</param>
/// <param name="Title">A title for the screen's frame.</param>
public sealed record TuiTreeRunRequest(
    object Node,
    bool ReturnOutcome = false,
    Func<IShellCallable, object?, object?>? Invoke = null,
    TimeSpan? RefreshInterval = null,
    string? Title = null,
    bool Plain = false,
    int? Width = null,
    int? Height = null);

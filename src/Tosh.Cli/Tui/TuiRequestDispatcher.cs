using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal static class TuiRequestDispatcher
{
    public static bool TryHandle(IReadOnlyList<object?> values, ToshRuntime runtime)
    {
        return TryHandle(values, runtime, out _);
    }

    public static bool TryHandle(IReadOnlyList<object?> values, ToshRuntime runtime, out IReadOnlyList<object?>? outcomeValues)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(runtime);

        outcomeValues = null;

        // The batch may also carry the screens a builder re-yielded on its way here.
        if (!TuiRequestProbe.TryGetRequest(values, out var value))
        {
            return false;
        }

        if (value is HelpBrowseRequest request)
        {
            TuiApplication.Run(new ConsoleTuiHost(), new HelpBrowserScreen(runtime, request), runtime.Config.ResolvedTerminal);
            return true;
        }

        if (value is ConfigBrowseRequest configRequest)
        {
            TuiApplication.Run(new ConsoleTuiHost(), new ConfigBrowserScreen(runtime, configRequest), runtime.Config.ResolvedTerminal);
            return true;
        }

        if (value is TuiPickRequest pickRequest)
        {
            var screen = new TuiPickScreen(pickRequest, runtime.Formatter);
            TuiApplication.Run(new ConsoleTuiHost(), screen, runtime.Config.ResolvedTerminal);
            outcomeValues = BuildOutcomeValues(screen.Outcome, pickRequest.ReturnOutcome);
            return true;
        }

        if (value is TuiConfirmRequest confirmRequest)
        {
            var screen = new TuiConfirmScreen(confirmRequest);
            TuiApplication.Run(new ConsoleTuiHost(), screen, runtime.Config.ResolvedTerminal);
            outcomeValues = BuildOutcomeValues(screen.Outcome, confirmRequest.ReturnOutcome);
            return true;
        }

        if (value is TuiInputRequest inputRequest)
        {
            var screen = new TuiInputScreen(inputRequest);
            TuiApplication.Run(new ConsoleTuiHost(), screen, runtime.Config.ResolvedTerminal);
            outcomeValues = BuildOutcomeValues(screen.Outcome, inputRequest.ReturnOutcome);
            return true;
        }

        if (value is TuiFilePickRequest fileRequest)
        {
            var screen = new TuiFilePickerScreen(fileRequest);
            TuiApplication.Run(new ConsoleTuiHost(), screen, runtime.Config.ResolvedTerminal);
            outcomeValues = BuildOutcomeValues(screen.Outcome, fileRequest.ReturnOutcome);
            return true;
        }

        if (value is TuiResetRequest)
        {
            // Straight to the terminal rather than through a screen: there is no screen,
            // and the point is to undo what a screen left behind.
            TuiTerminalRepair.Repair(Console.Out.Write);
            outcomeValues = null;
            return true;
        }

        if (value is TuiTreeRunRequest treeRequest)
        {
            // A record tree: built here, where the widget registry lives.
            var root = TuiTreeBuilder.Build(treeRequest.Node, null, treeRequest.Invoke, out var bindings);

            var screen = new TuiDeclarativeScreen(
                root,
                bindings,
                treeRequest.Invoke,
                treeRequest.Title,
                treeRequest.RefreshInterval);

            if (treeRequest.Plain)
            {
                // One frame, as the text of it. A screen is already pure — `Render` takes
                // a size and answers with a buffer — so this needs no terminal, which is
                // the whole reason for having it (`TUI-0009`).
                outcomeValues = [screen.Render(PlainSize(treeRequest)).ToPlainText()];
                return true;
            }

            TuiApplication.Run(new ConsoleTuiHost(), screen, runtime.Config.ResolvedTerminal);

            outcomeValues = screen.HasForm && !treeRequest.ReturnOutcome
                ? null
                : BuildOutcomeValues(screen.Outcome, treeRequest.ReturnOutcome);

            return true;
        }

        return false;
    }

    /// <summary>How big to draw a screen that nothing is going to display.</summary>
    /// <remarks>
    /// The terminal's size when there is one, because a reader piping a screen into
    /// <c>less</c> wants it the shape of their window. 80x24 otherwise — which is what a
    /// terminal was when the size stopped being a guess, and is still the width anything
    /// reading the output will assume.
    /// </remarks>
    private static TuiSize PlainSize(TuiTreeRunRequest request)
    {
        var width = request.Width ?? Fallback(() => Console.WindowWidth, 80);
        var height = request.Height ?? Fallback(() => Console.WindowHeight, 24);

        return new TuiSize(Math.Max(1, width), Math.Max(1, height));

        // Redirected output has no window, and asking throws rather than answering zero.
        static int Fallback(Func<int> ask, int otherwise)
        {
            try
            {
                var value = ask();
                return value > 0 ? value : otherwise;
            }
            catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
            {
                return otherwise;
            }
        }
    }

    private static IReadOnlyList<object?>? BuildOutcomeValues(TuiScreenOutcome? outcome, bool returnOutcome)
    {
        if (outcome is null)
        {
            return null;
        }

        if (outcome.Cancelled)
        {
            return returnOutcome ? [outcome] : null;
        }

        if (returnOutcome)
        {
            return [outcome];
        }

        // Raw mode: yield selected items directly
        if (outcome.Selected.Count > 0)
        {
            return outcome.Selected.ToArray();
        }

        // For input/confirm, check values dict
        if (outcome.Values.Count > 0)
        {
            return outcome.Values.Values.ToArray();
        }

        return null;
    }
}

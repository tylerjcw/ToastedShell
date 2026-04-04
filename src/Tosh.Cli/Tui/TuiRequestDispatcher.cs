using Tosh.Core;
using Tosh.Tui;
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

        if (values.Count != 1)
        {
            return false;
        }

        if (values[0] is HelpBrowseRequest request)
        {
            TuiApplication.Run(new ConsoleTuiHost(), new HelpBrowserScreen(runtime, request));
            return true;
        }

        if (values[0] is ConfigBrowseRequest configRequest)
        {
            TuiApplication.Run(new ConsoleTuiHost(), new ConfigBrowserScreen(runtime, configRequest));
            return true;
        }

        if (values[0] is TuiPickRequest pickRequest)
        {
            var screen = new TuiPickScreen(pickRequest, runtime.Formatter);
            TuiApplication.Run(new ConsoleTuiHost(), screen);
            outcomeValues = BuildOutcomeValues(screen.Outcome, pickRequest.ReturnOutcome);
            return true;
        }

        if (values[0] is TuiConfirmRequest confirmRequest)
        {
            var screen = new TuiConfirmScreen(confirmRequest);
            TuiApplication.Run(new ConsoleTuiHost(), screen);
            outcomeValues = BuildOutcomeValues(screen.Outcome, confirmRequest.ReturnOutcome);
            return true;
        }

        if (values[0] is TuiInputRequest inputRequest)
        {
            var screen = new TuiInputScreen(inputRequest);
            TuiApplication.Run(new ConsoleTuiHost(), screen);
            outcomeValues = BuildOutcomeValues(screen.Outcome, inputRequest.ReturnOutcome);
            return true;
        }

        if (values[0] is TuiFilePickRequest fileRequest)
        {
            var screen = new TuiFilePickerScreen(fileRequest);
            TuiApplication.Run(new ConsoleTuiHost(), screen);
            outcomeValues = BuildOutcomeValues(screen.Outcome, fileRequest.ReturnOutcome);
            return true;
        }

        if (values[0] is TuiRunRequest runRequest)
        {
            var screen = new TuiCustomScreen(runRequest);
            TuiApplication.Run(new ConsoleTuiHost(), screen);
            outcomeValues = BuildOutcomeValues(screen.Outcome, runRequest.ReturnOutcome);
            return true;
        }

        return false;
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

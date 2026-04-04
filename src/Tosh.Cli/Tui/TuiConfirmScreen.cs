using System.Text;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class TuiConfirmScreen : ITuiScreen
{
    private readonly TuiConfirmRequest _request;
    private readonly TuiConfirmationDialogState _dialog = new();

    public TuiConfirmScreen(TuiConfirmRequest request)
    {
        _request = request;
        _dialog.Open(
            title: "Confirmation",
            message: request.Message,
            confirmLabel: request.ConfirmLabel,
            cancelLabel: request.CancelLabel,
            confirmSelected: request.DefaultConfirm);
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        var sb = new StringBuilder();
        var width = size.Width;
        var height = size.Height;

        // Center the dialog vertically
        var entries = _dialog.BuildEntries(Math.Min(width, 60));
        var startRow = Math.Max(0, (height - entries.Count) / 2);
        var padding = Math.Max(0, (width - 60) / 2);
        var indent = new string(' ', padding);

        for (var i = 0; i < startRow; i++)
        {
            sb.AppendLine();
        }

        foreach (var entry in entries)
        {
            sb.AppendLine(indent + entry);
        }

        return new TuiFrame(sb.ToString());
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        var result = _dialog.HandleKey(key);

        switch (result.Kind)
        {
            case TuiConfirmationDialogResultKind.Confirmed:
                Outcome = new TuiScreenOutcome
                {
                    Selected = [true],
                    Cancelled = false,
                    Values = new Dictionary<string, object?> { ["confirmed"] = true },
                };
                return TuiScreenResult.Exit;

            case TuiConfirmationDialogResultKind.Cancelled:
                Outcome = new TuiScreenOutcome
                {
                    Selected = [false],
                    Cancelled = true,
                    Values = new Dictionary<string, object?> { ["confirmed"] = false },
                };
                return TuiScreenResult.Exit;

            default:
                return TuiScreenResult.Continue;
        }
    }
}

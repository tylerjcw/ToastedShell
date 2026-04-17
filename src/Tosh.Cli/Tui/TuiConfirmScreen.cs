using System.Text;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class TuiConfirmScreen : ITuiScreen
{
    private readonly TuiConfirmRequest _request;
    private readonly TuiConfirmationDialogState _dialog = new();
    private int _buttonRowScreen;
    private int _confirmButtonStart;
    private int _confirmButtonEnd;
    private int _cancelButtonStart;
    private int _cancelButtonEnd;

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
        var dialogWidth = Math.Min(width, 60);
        var entries = _dialog.BuildEntries(dialogWidth);
        var startRow = Math.Max(0, (height - entries.Count) / 2);
        var padding = Math.Max(0, (width - 60) / 2);
        var indent = new string(' ', padding);

        // Track button positions for mouse hit-testing
        // Button row is: "> [Confirm]     [Cancel]" — find it
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Contains($"[{_dialog.ConfirmLabel}]"))
            {
                _buttonRowScreen = startRow + i;
                var confirmIdx = entry.IndexOf($"[{_dialog.ConfirmLabel}]", StringComparison.Ordinal);
                _confirmButtonStart = padding + confirmIdx;
                _confirmButtonEnd = _confirmButtonStart + _dialog.ConfirmLabel.Length + 2;
                var cancelIdx = entry.IndexOf($"[{_dialog.CancelLabel}]", StringComparison.Ordinal);
                _cancelButtonStart = padding + cancelIdx;
                _cancelButtonEnd = _cancelButtonStart + _dialog.CancelLabel.Length + 2;
                break;
            }
        }

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

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
            return HandleKey(input.Key);

        var mouse = input.Mouse;

        if (mouse.Action == TuiMouseAction.Press && mouse.Button == TuiMouseButton.Left &&
            mouse.Row == _buttonRowScreen)
        {
            if (mouse.Column >= _confirmButtonStart && mouse.Column < _confirmButtonEnd)
            {
                _dialog.ConfirmSelected = true;
                // Simulate Enter to confirm
                return HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
            }

            if (mouse.Column >= _cancelButtonStart && mouse.Column < _cancelButtonEnd)
            {
                _dialog.ConfirmSelected = false;
                return HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
            }
        }

        return TuiScreenResult.Continue;
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

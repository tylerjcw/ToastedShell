using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

/// <summary>
/// The confirmation dialog behind <c>tui confirm</c>.
/// </summary>
/// <remarks>
/// The first screen drawn into a <see cref="TuiBuffer"/> rather than concatenated into a
/// string (<c>TUI-0001</c>). Two things changed as a consequence, and both were defects
/// of the string model rather than of this screen:
/// <list type="bullet">
///   <item>
///     It used to find its own buttons by searching the text it had just rendered for
///     <c>[Confirm]</c>, then cache five integers describing where they were. Drawing
///     into cells means the rectangles are known while drawing, so they are recorded
///     rather than recovered.
///   </item>
///   <item>
///     The dialog is now drawn onto a cleared background, so it is a dialog rather than
///     a screenful of text with blank lines above it.
///   </item>
/// </list>
/// </remarks>
internal sealed class TuiConfirmScreen : ITuiScreen
{
    private const int MaxDialogWidth = 60;

    private readonly TuiConfirmRequest _request;
    private readonly TuiConfirmationDialogState _dialog = new();
    private TuiRect _confirmButton;
    private TuiRect _cancelButton;

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
        var buffer = new TuiBuffer(size);

        var dialogWidth = Math.Min(size.Width, MaxDialogWidth);
        var entries = _dialog.BuildEntries(dialogWidth);
        var top = Math.Max(0, (size.Height - entries.Count) / 2);
        var left = Math.Max(0, (size.Width - dialogWidth) / 2);

        var surface = new TuiSurface(buffer, new TuiRect(left, top, dialogWidth, entries.Count));

        for (var row = 0; row < entries.Count; row += 1)
        {
            surface.DrawText(0, row, entries[row], TuiStyle.Default);
        }

        RecordButtonPositions(entries, left, top);

        return new TuiFrame(buffer);
    }

    /// <summary>
    /// Notes where the buttons were drawn, so a click can be matched against them.
    /// </summary>
    /// <remarks>
    /// The offsets are computed from the same pieces the label row is built from rather
    /// than by searching the rendered line, which is what this screen used to do. The
    /// row is <c>"&gt; [Confirm]    &gt; [Cancel]"</c>: a marker, a space, then each
    /// bracketed label, separated by four spaces.
    /// </remarks>
    private void RecordButtonPositions(IReadOnlyList<string> entries, int left, int top)
    {
        var buttonRow = -1;

        for (var row = 0; row < entries.Count; row += 1)
        {
            if (entries[row].Contains($"[{_dialog.ConfirmLabel}]", StringComparison.Ordinal))
            {
                buttonRow = row;
                break;
            }
        }

        if (buttonRow < 0)
        {
            _confirmButton = default;
            _cancelButton = default;
            return;
        }

        var confirmWidth = _dialog.ConfirmLabel.Length + 2;
        var confirmLeft = left + 2;
        var cancelLeft = confirmLeft + confirmWidth + 4 + 2;

        _confirmButton = new TuiRect(confirmLeft, top + buttonRow, confirmWidth, 1);
        _cancelButton = new TuiRect(cancelLeft, top + buttonRow, _dialog.CancelLabel.Length + 2, 1);
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
        {
            return HandleKey(input.Key);
        }

        var mouse = input.Mouse;

        if (mouse.Action != TuiMouseAction.Press || mouse.Button != TuiMouseButton.Left)
        {
            return TuiScreenResult.Continue;
        }

        if (_confirmButton.Contains(mouse.Column, mouse.Row))
        {
            _dialog.ConfirmSelected = true;
            return Decide(confirmed: true);
        }

        if (_cancelButton.Contains(mouse.Column, mouse.Row))
        {
            _dialog.ConfirmSelected = false;
            return Decide(confirmed: false);
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        var result = _dialog.HandleKey(key);

        return result.Kind switch
        {
            TuiConfirmationDialogResultKind.Confirmed => Decide(confirmed: true),
            TuiConfirmationDialogResultKind.Cancelled => Decide(confirmed: false),
            _ => TuiScreenResult.Continue,
        };
    }

    private TuiScreenResult Decide(bool confirmed)
    {
        Outcome = new TuiScreenOutcome
        {
            Selected = [confirmed],
            Cancelled = !confirmed,
            Values = new Dictionary<string, object?> { ["confirmed"] = confirmed },
        };

        return TuiScreenResult.Exit;
    }
}

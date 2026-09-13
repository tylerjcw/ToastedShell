using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// The confirmation dialog behind <c>tui confirm</c>.
/// </summary>
/// <remarks>
/// <para>
/// The first screen built from widgets rather than drawn by hand (<c>TUI-0002</c>), and
/// the third shape it has had. It began by concatenating lines into a string and finding
/// its own buttons by searching that string for <c>[Confirm]</c>; then it drew into a
/// cell buffer and recorded the button rectangles while drawing; now it declares a
/// layout and the buttons answer for themselves.
/// </para>
/// <para>
/// Nothing here computes a coordinate. The dialog is sized to its message because the
/// border asks its child how big it is, the buttons are found by hit testing the
/// arrangement, and a click is handled by the button that was clicked.
/// </para>
/// </remarks>
internal sealed class TuiConfirmScreen : ITuiScreen
{
    private const int MaxDialogWidth = 60;

    private readonly TuiConfirmRequest _request;
    private readonly TuiButton _confirm;
    private readonly TuiButton _cancel;
    private readonly TuiBorder _dialog;

    public TuiConfirmScreen(TuiConfirmRequest request)
    {
        _request = request;

        _confirm = new TuiButton(request.ConfirmLabel, () => Decide(confirmed: true))
        {
            IsSelected = request.DefaultConfirm,
        };

        _cancel = new TuiButton(request.CancelLabel, () => Decide(confirmed: false))
        {
            IsSelected = !request.DefaultConfirm,
        };

        var buttons = new TuiStack(TuiOrientation.Horizontal) { Gap = 4 }
            .Add(_confirm, TuiLength.Auto)
            .Add(_cancel, TuiLength.Auto);

        var body = new TuiStack(TuiOrientation.Vertical)
            .Add(new TuiTextWidget(request.Message), TuiLength.Auto)
            .Add(new TuiTextWidget(string.Empty), TuiLength.Fixed(1))
            .Add(buttons, TuiLength.Fixed(1))
            .Add(new TuiTextWidget(string.Empty), TuiLength.Fixed(1))
            .Add(
                new TuiTextWidget("Left/Right or Tab switches the selection. Enter confirms. Esc cancels.")
                {
                    Style = new TuiStyle(Attributes: TuiTextAttributes.Dim),
                },
                TuiLength.Auto);

        _dialog = new TuiBorder(body, "Confirmation");
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        var buffer = new TuiBuffer(size);

        // Measured, then centred at whatever size it asked for. Nothing here knows how
        // tall a wrapped message is; the widgets do.
        var width = Math.Min(size.Width, MaxDialogWidth);
        var desired = _dialog.Measure(new TuiConstraints(width, size.Height));

        var bounds = new TuiRect(
            Math.Max(0, (size.Width - desired.Width) / 2),
            Math.Max(0, (size.Height - desired.Height) / 2),
            desired.Width,
            desired.Height);

        _dialog.Arrange(bounds);
        _dialog.Draw(new TuiSurface(buffer, bounds));

        return new TuiFrame(buffer);
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
        {
            return HandleKey(input.Key);
        }

        var mouse = input.Mouse;

        // The widget under the pointer handles its own click. No stored rectangles.
        if (mouse.Action == TuiMouseAction.Press &&
            mouse.Button == TuiMouseButton.Left &&
            _dialog.HitTest(mouse.Column, mouse.Row) is { } hit)
        {
            if (hit.OnInput(input) && Outcome is not null)
            {
                return TuiScreenResult.Exit;
            }
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
            case ConsoleKey.Tab:
                Select(!_confirm.IsSelected);
                return TuiScreenResult.Continue;

            case ConsoleKey.Y:
                Select(confirm: true);
                return Decide(confirmed: true);

            case ConsoleKey.N:
                Select(confirm: false);
                return Decide(confirmed: false);

            case ConsoleKey.Escape:
                return Decide(confirmed: false);

            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar:
                return Decide(confirmed: _confirm.IsSelected);

            default:
                return TuiScreenResult.Continue;
        }
    }

    private void Select(bool confirm)
    {
        _confirm.IsSelected = confirm;
        _cancel.IsSelected = !confirm;
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

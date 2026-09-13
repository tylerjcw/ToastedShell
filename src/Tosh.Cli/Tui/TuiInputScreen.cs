using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// The text prompt behind <c>tui input</c>.
/// </summary>
/// <remarks>
/// Built from widgets (<c>TUI-0002</c>). The field owns the text and the caret; the
/// border sizes itself to the prompt and the value, so a multiline entry grows the box
/// downwards instead of running into the footer.
/// </remarks>
internal sealed class TuiInputScreen : ITuiScreen
{
    private const int MaxDialogWidth = 70;

    private readonly TuiTextField _field;
    private readonly TuiBorder _dialog;
    private readonly TuiFocus _focus;

    public TuiInputScreen(TuiInputRequest request)
    {
        _field = new TuiTextField(request.DefaultValue ?? string.Empty)
        {
            Mask = request.Password,
            Multiline = request.Multiline,
            Submitted = text => Finish(new TuiScreenOutcome
            {
                Selected = [text],
                Cancelled = false,
                Values = new Dictionary<string, object?> { ["text"] = text },
            }),
            Cancelled = () => Finish(new TuiScreenOutcome { Cancelled = true }),
        };

        var body = new TuiStack(TuiOrientation.Vertical)
            .Add(_field, TuiLength.Auto)
            .Add(new TuiTextWidget(string.Empty), TuiLength.Fixed(1))
            .Add(
                new TuiTextWidget(request.Multiline
                    ? "Ctrl+Enter submits   Enter adds a line   Esc cancels"
                    : "Enter submits   Esc cancels")
                {
                    Style = new TuiStyle(Attributes: TuiTextAttributes.Dim),
                },
                TuiLength.Auto);

        _dialog = new TuiBorder(body, request.Prompt ?? "Enter text")
        {
            TitleStyle = new TuiStyle(Attributes: TuiTextAttributes.Bold),
        };

        _focus = new TuiFocus(_dialog);
        _focus.Focus(_field);
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        var buffer = new TuiBuffer(size);

        var width = Math.Min(size.Width, MaxDialogWidth);
        var desired = _dialog.Measure(new TuiConstraints(width, size.Height));

        // Wide enough for the prompt and the value, tall enough for however many lines
        // the value currently has — the widgets work that out, not this screen.
        var bounds = new TuiRect(
            Math.Max(0, (size.Width - desired.Width) / 2),
            Math.Max(0, (size.Height - desired.Height) / 2),
            desired.Width,
            desired.Height);

        _dialog.Arrange(bounds);
        _dialog.Paint(new TuiSurface(buffer, bounds));

        buffer.Cursor = (
            _field.Bounds.Left + _field.CaretColumn,
            _field.Bounds.Top + _field.CaretRow);

        return new TuiFrame(buffer);
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (_focus.Dispatch(input))
        {
            return Outcome is null ? TuiScreenResult.Continue : TuiScreenResult.Exit;
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
        => HandleInput(TuiInputEvent.FromKey(key));

    private void Finish(TuiScreenOutcome outcome) => Outcome = outcome;
}

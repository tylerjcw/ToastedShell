using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// The file browser behind <c>tui file</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built from widgets (<c>TUI-0002</c>), with one division of labour worth stating:
/// <see cref="TuiFilePickerState"/> keeps the model — which directory, what is in it,
/// what happens when you press Right — and the widgets draw it. Entering a directory is
/// not something a list widget should know how to do.
/// </para>
/// <para>
/// So keys go to the state and the list is not focusable here: if it were, both would
/// act on an arrow key and the selection would move twice.
/// </para>
/// </remarks>
internal sealed class TuiFilePickerScreen : ITuiScreen
{
    private const int InteractionPageSize = 20;

    private readonly TuiFilePickerState _picker = new();
    private readonly TuiList _list;
    private readonly TuiTextWidget _status;
    private readonly TuiBorder _frame;
    private readonly TuiStack _root;

    public TuiFilePickerScreen(TuiFilePickRequest request)
    {
        var selectionMode = request.DirectoryOnly
            ? TuiFilePickerSelectionMode.Directory
            : TuiFilePickerSelectionMode.Any;

        _picker.Open(
            request.InitialPath ?? Environment.CurrentDirectory,
            selectionMode,
            initialSelectionPath: null,
            pageSize: InteractionPageSize);

        _list = new TuiList
        {
            // The state owns the selection; the list reports a click and redraws.
            SelectionChanged = _picker.SelectIndex,
        };

        _status = new TuiTextWidget { Style = new TuiStyle(Attributes: TuiTextAttributes.Dim) };
        _frame = new TuiBorder(_list) { TitleStyle = new TuiStyle(Attributes: TuiTextAttributes.Bold) };

        _root = new TuiStack(TuiOrientation.Vertical)
            .Add(_frame, TuiLength.Star())
            .Add(_status, TuiLength.Auto);
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        var buffer = new TuiBuffer(size);
        var bounds = new TuiRect(0, 0, size.Width, size.Height);

        _list.Items = [.. _picker.ItemLabels];
        _list.SelectedIndex = _picker.SelectedIndex;

        _frame.Title = $"{_picker.CurrentDirectory}   ({_picker.SelectionMode})";

        _status.Text = string.IsNullOrWhiteSpace(_picker.StatusMessage)
            ? "Up/Down move   Right enters   Left goes up   Enter opens or selects   Space selects   Esc cancels"
            : $"{_picker.StatusMessage}\nUp/Down move   Right enters   Left goes up   Enter opens or selects   Esc cancels";

        _root.Measure(TuiConstraints.From(size));
        _root.Arrange(bounds);
        _root.Draw(new TuiSurface(buffer, bounds));

        return new TuiFrame(buffer);
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
        {
            return HandleKey(input.Key);
        }

        var mouse = input.Mouse;

        // The wheel moves the choice rather than the view, as it does in the picker: the
        // point of the screen is to land on a path.
        if (mouse.Action == TuiMouseAction.Scroll)
        {
            if (mouse.Button == TuiMouseButton.ScrollUp)
            {
                _picker.MovePrevious();
            }
            else
            {
                _picker.MoveNext();
            }

            return TuiScreenResult.Continue;
        }

        // The list converts the click through its viewport and tells the state.
        _list.OnInput(input);

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        var result = _picker.HandleKey(key, InteractionPageSize);

        switch (result.Kind)
        {
            case TuiFilePickerResultKind.Selected:
                Outcome = new TuiScreenOutcome
                {
                    Selected = [result.Path],
                    Cancelled = false,
                    Values = new Dictionary<string, object?> { ["path"] = result.Path },
                };
                return TuiScreenResult.Exit;

            case TuiFilePickerResultKind.Cancelled:
                Outcome = new TuiScreenOutcome { Cancelled = true };
                return TuiScreenResult.Exit;

            default:
                // The list keeps the highlighted row on screen itself.
                return TuiScreenResult.Continue;
        }
    }
}

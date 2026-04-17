using System.Text;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class TuiFilePickerScreen : ITuiScreen
{
    private readonly TuiFilePickRequest _request;
    private readonly TuiFilePickerState _picker = new();
    private int _headerLines;
    private int _pageSize;

    public TuiFilePickerScreen(TuiFilePickRequest request)
    {
        _request = request;

        var selectionMode = request.DirectoryOnly
            ? TuiFilePickerSelectionMode.Directory
            : TuiFilePickerSelectionMode.Any;

        var startDirectory = request.InitialPath ?? Environment.CurrentDirectory;
        _picker.Open(startDirectory, selectionMode, initialSelectionPath: null, pageSize: 20);
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        var sb = new StringBuilder();
        var width = size.Width;
        var height = size.Height;

        var entries = _picker.BuildEntries(width, height);

        // Track header lines for mouse click offset (Location, Mode, optional Status, blank)
        _headerLines = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            _headerLines++;
            if (entries[i].Length == 0) break; // blank line after header
        }

        _pageSize = Math.Max(1, height - 7);

        foreach (var entry in entries)
        {
            sb.AppendLine(entry.Length > width ? entry[..width] : entry);
        }

        return new TuiFrame(sb.ToString());
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
            return HandleKey(input.Key);

        var mouse = input.Mouse;

        // Scroll wheel navigates the file list
        if (mouse.Action == TuiMouseAction.Scroll)
        {
            if (mouse.Button == TuiMouseButton.ScrollUp)
                _picker.MovePrevious();
            else if (mouse.Button == TuiMouseButton.ScrollDown)
                _picker.MoveNext();

            return TuiScreenResult.Continue;
        }

        // Click on a file entry to select it
        if (mouse.Action == TuiMouseAction.Press && mouse.Button == TuiMouseButton.Left)
        {
            var listRow = mouse.Row - _headerLines;
            var range = _picker.Scroll.GetVisibleRange();

            if (listRow >= 0 && listRow < range.Length)
            {
                _picker.SelectIndex(range.Start + listRow);
                return TuiScreenResult.Continue;
            }
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        var pageSize = 20; // Stable page size for interaction
        var result = _picker.HandleKey(key, pageSize);

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
                return TuiScreenResult.Continue;
        }
    }
}

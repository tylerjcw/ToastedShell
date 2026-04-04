using System.Text;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class TuiFilePickerScreen : ITuiScreen
{
    private readonly TuiFilePickRequest _request;
    private readonly TuiFilePickerState _picker = new();

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

        foreach (var entry in entries)
        {
            sb.AppendLine(entry.Length > width ? entry[..width] : entry);
        }

        return new TuiFrame(sb.ToString());
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

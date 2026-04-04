using System.Text;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class TuiInputScreen : ITuiScreen
{
    private readonly TuiInputRequest _request;
    private readonly TuiTextInputState _input = new();

    public TuiInputScreen(TuiInputRequest request)
    {
        _request = request;
        _input.SetText(request.DefaultValue);
    }

    public TuiScreenOutcome? Outcome { get; private set; }

    public TuiFrame Render(TuiSize size)
    {
        var sb = new StringBuilder();
        var width = size.Width;
        var height = size.Height;

        // Center content vertically
        var contentLines = 4;
        var startRow = Math.Max(0, (height - contentLines) / 2);

        for (var i = 0; i < startRow; i++)
        {
            sb.AppendLine();
        }

        var prompt = _request.Prompt ?? "Enter text:";
        sb.AppendLine(prompt.Length > width ? prompt[..width] : prompt);
        sb.AppendLine();

        var inputLine = _input.RenderWithCursor();
        sb.AppendLine(inputLine.Length > width ? inputLine[..width] : inputLine);
        sb.AppendLine();
        sb.Append("Enter: submit | Esc: cancel");

        return new TuiFrame(sb.ToString());
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        var result = _input.HandleKey(key);

        switch (result)
        {
            case TuiTextInputResult.Submit:
                Outcome = new TuiScreenOutcome
                {
                    Selected = [_input.Text],
                    Cancelled = false,
                    Values = new Dictionary<string, object?> { ["text"] = _input.Text },
                };
                return TuiScreenResult.Exit;

            case TuiTextInputResult.Cancel:
                Outcome = new TuiScreenOutcome
                {
                    Cancelled = true,
                };
                return TuiScreenResult.Exit;

            default:
                return TuiScreenResult.Continue;
        }
    }
}

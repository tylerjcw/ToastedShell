using System.Text;
using Tosh.Core;

namespace Tosh.Cli;

internal static class ConsolePager
{
    private const string EnterAlternateScreen = "\u001b[?1049h";
    private const string ExitAlternateScreen = "\u001b[?1049l";
    private const string ClearScreenAndHome = "\u001b[2J\u001b[H";
    private const string EnableSgrMouse = "\u001b[?1000h\u001b[?1006h";
    private const string DisableSgrMouse = "\u001b[?1000l\u001b[?1006l";

    public static bool ShouldPage(string rendered, int? availableHeight, ToshPagingConfig config, bool isOutputRedirected)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (isOutputRedirected || !config.Enabled || string.IsNullOrEmpty(rendered) || availableHeight is not int height)
        {
            return false;
        }

        var pageSize = GetPageSize(height, config.ReservedLines);
        return CountLines(rendered) > pageSize;
    }

    public static async Task WriteAsync(string rendered, int? availableHeight, ToshPagingConfig config, ToshTextStyleConfig promptStyle)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(promptStyle);

        var lines = SplitLines(rendered);
        var pageSize = GetPageSize(availableHeight ?? lines.Count, config.ReservedLines);
        var state = new PagerState(lines, pageSize);

        Console.Write(EnterAlternateScreen);
        Console.Write(EnableSgrMouse);

        var inputReader = new Tui.TuiInputReader();

        try
        {
            while (true)
            {
                Console.Write(RenderViewport(state, promptStyle, config.ReservedLines));
                var input = inputReader.Read();

                if (input.IsMouse)
                {
                    var mouse = input.Mouse;

                    if (mouse.Action == Tui.TuiMouseAction.Scroll)
                    {
                        if (mouse.Button == Tui.TuiMouseButton.ScrollUp)
                            state.PreviousLine();
                        else if (mouse.Button == Tui.TuiMouseButton.ScrollDown)
                            state.NextLine();
                    }

                    continue;
                }

                if (!TryApplyKey(state, input.Key))
                {
                    break;
                }
            }
        }
        finally
        {
            Console.Write(DisableSgrMouse);
            Console.Write(ExitAlternateScreen);
        }

        await Task.CompletedTask;
    }

    internal static int CountLines(string rendered)
    {
        return SplitLines(rendered).Count;
    }

    internal static int GetPageSize(int availableHeight, int reservedLines)
    {
        return Math.Max(1, availableHeight - 1 - Math.Max(0, reservedLines));
    }

    internal static IReadOnlyList<string> SplitLines(string rendered)
    {
        return rendered
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    internal static string RenderViewport(PagerState state, ToshTextStyleConfig promptStyle, int reservedLines = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(promptStyle);

        var builder = new StringBuilder();
        builder.Append(ClearScreenAndHome);

        foreach (var line in state.GetVisibleLines())
        {
            builder.AppendLine(line);
        }

        for (var index = 0; index < Math.Max(0, reservedLines); index++)
        {
            builder.AppendLine();
        }

        builder.Append(promptStyle.Apply(BuildFooterText(state)).ToAnsi());
        return builder.ToString();
    }

    internal static string BuildFooterText(PagerState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var start = state.LineCount == 0 ? 0 : state.StartIndex + 1;
        var end = Math.Min(state.StartIndex + state.PageSize, state.LineCount);
        return $"-- more -- {start}-{end}/{state.LineCount}  Space/PgDn next  b/PgUp prev  Enter/Up/Down line  g/G home/end  q quit";
    }

    internal static bool TryApplyKey(PagerState state, ConsoleKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(state);

        switch (key.Key)
        {
            case ConsoleKey.Q:
            case ConsoleKey.Escape:
                return false;
            case ConsoleKey.Spacebar:
            case ConsoleKey.PageDown:
                state.NextPage();
                return true;
            case ConsoleKey.Enter:
            case ConsoleKey.DownArrow:
                state.NextLine();
                return true;
            case ConsoleKey.PageUp:
                state.PreviousPage();
                return true;
            case ConsoleKey.UpArrow:
                state.PreviousLine();
                return true;
            case ConsoleKey.Home:
                state.Home();
                return true;
            case ConsoleKey.End:
                state.End();
                return true;
            case ConsoleKey.G:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    state.End();
                }
                else
                {
                    state.Home();
                }

                return true;
            case ConsoleKey.B:
                state.PreviousPage();
                return true;
            default:
                if (key.KeyChar == 'g')
                {
                    state.Home();
                    return true;
                }

                if (key.KeyChar == 'G')
                {
                    state.End();
                    return true;
                }

                if (key.KeyChar == 'b')
                {
                    state.PreviousPage();
                    return true;
                }

                return true;
        }
    }

    internal sealed class PagerState
    {
        public PagerState(IReadOnlyList<string> lines, int pageSize)
        {
            Lines = lines ?? throw new ArgumentNullException(nameof(lines));
            PageSize = Math.Max(1, pageSize);
        }

        public IReadOnlyList<string> Lines { get; }

        public int PageSize { get; }

        public int StartIndex { get; private set; }

        public int LineCount => Lines.Count;

        public IReadOnlyList<string> GetVisibleLines()
        {
            return Lines.Skip(StartIndex).Take(PageSize).ToArray();
        }

        public void NextLine()
        {
            StartIndex = Math.Min(StartIndex + 1, GetMaxStartIndex());
        }

        public void PreviousLine()
        {
            StartIndex = Math.Max(0, StartIndex - 1);
        }

        public void NextPage()
        {
            StartIndex = Math.Min(StartIndex + PageSize, GetMaxStartIndex());
        }

        public void PreviousPage()
        {
            StartIndex = Math.Max(0, StartIndex - PageSize);
        }

        public void Home()
        {
            StartIndex = 0;
        }

        public void End()
        {
            StartIndex = GetMaxStartIndex();
        }

        private int GetMaxStartIndex()
        {
            return Math.Max(0, LineCount - PageSize);
        }
    }
}

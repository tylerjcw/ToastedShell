namespace Tosh.Cli;

public sealed class ReplLineEditor
{
    private const string SaveCursorPosition = "\u001b[s";
    private const string RestoreCursorPosition = "\u001b[u";
    private const string ClearToEndOfScreen = "\u001b[J";

    public string? ReadLine(string prompt, IReadOnlyList<string> history)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(history);

        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        var buffer = new LineEditorBuffer();
        var historyNavigator = new LineEditorHistory(history);

        Console.Write(SaveCursorPosition);
        Render(prompt, buffer);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                PositionCursor(prompt.Length + buffer.Text.Length);
                Console.WriteLine();
                return buffer.Text;
            }

            if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.D && buffer.Text.Length == 0)
            {
                PositionCursor(prompt.Length + buffer.Text.Length);
                Console.WriteLine();
                return null;
            }

            var shouldRender = HandleKey(buffer, historyNavigator, key);

            if (shouldRender)
            {
                Render(prompt, buffer);
            }
        }
    }

    private static bool HandleKey(LineEditorBuffer buffer, LineEditorHistory historyNavigator, ConsoleKeyInfo key)
    {
        if (key.Modifiers == ConsoleModifiers.Control)
        {
            return key.Key switch
            {
                ConsoleKey.A => buffer.MoveHome(),
                ConsoleKey.E => buffer.MoveEnd(),
                ConsoleKey.U => buffer.Clear(),
                ConsoleKey.W => buffer.DeleteWordBackward(),
                ConsoleKey.K => buffer.KillToEnd(),
                ConsoleKey.L => ClearScreen(),
                _ => false,
            };
        }

        return key.Key switch
        {
            ConsoleKey.Backspace => buffer.Backspace(),
            ConsoleKey.Delete => buffer.Delete(),
            ConsoleKey.LeftArrow => buffer.MoveLeft(),
            ConsoleKey.RightArrow => buffer.MoveRight(),
            ConsoleKey.Home => buffer.MoveHome(),
            ConsoleKey.End => buffer.MoveEnd(),
            ConsoleKey.UpArrow => historyNavigator.TryPrevious(buffer.Text, out var previous) && ApplyHistory(buffer, previous),
            ConsoleKey.DownArrow => historyNavigator.TryNext(out var next) && ApplyHistory(buffer, next),
            _ => HandleCharacterInput(buffer, key),
        };
    }

    private static bool ApplyHistory(LineEditorBuffer buffer, string text)
    {
        buffer.SetText(text);
        return true;
    }

    private static bool HandleCharacterInput(LineEditorBuffer buffer, ConsoleKeyInfo key)
    {
        if (!char.IsControl(key.KeyChar))
        {
            return buffer.Insert(key.KeyChar);
        }

        return false;
    }

    private static int Render(string prompt, LineEditorBuffer buffer)
    {
        var text = prompt + buffer.Text;
        var currentRenderLength = text.Length;

        Console.Write(RestoreCursorPosition);
        Console.Write(ClearToEndOfScreen);
        Console.Write(text);
        PositionCursor(prompt.Length + buffer.CursorIndex);
        return currentRenderLength;
    }

    private static void PositionCursor(int offset)
    {
        var width = GetConsoleWidth();
        var row = offset / width;
        var column = offset % width;

        Console.Write(RestoreCursorPosition);

        if (row > 0)
        {
            Console.Write($"\u001b[{row}B");
        }

        if (column > 0)
        {
            Console.Write($"\u001b[{column}C");
        }
    }

    private static bool ClearScreen()
    {
        try
        {
            Console.Clear();
        }
        catch
        {
        }

        return true;
    }

    private static int GetConsoleWidth()
    {
        try
        {
            if (Console.BufferWidth > 0)
            {
                return Console.BufferWidth;
            }
        }
        catch
        {
        }

        try
        {
            if (Console.WindowWidth > 0)
            {
                return Console.WindowWidth;
            }
        }
        catch
        {
        }

        return 80;
    }
}

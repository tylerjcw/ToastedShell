using System.Text;
using System.Text.RegularExpressions;
using Tosh.Core;

namespace Tosh.Cli;

public sealed class ReplLineEditor
{
    private const string ClearToEndOfScreen = "\u001b[J";
    private static readonly ToshCompletionThemeConfig DefaultCompletionTheme = new();

    private static readonly Regex AnsiEscapePattern = new(@"\x1b\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);

    public string? ReadLine(
        string prompt,
        IReadOnlyList<string> history,
        Func<string, int, ReplCompletionResult?>? completionProvider = null,
        string initialText = "",
        int? initialCursorIndex = null,
        Func<string, string>? highlighter = null,
        string continuationPrompt = "....> ",
        int maxVisibleSuggestions = 8,
        bool showGhostText = true,
        ToshCompletionThemeConfig? completionTheme = null,
        Func<string, ReplContinuationState>? continuationHandler = null,
        Func<LineEditorBuffer, ConsoleKeyInfo, bool>? specialKeyHandler = null,
        Action<LineEditorBuffer>? onBufferActivated = null,
        Action<LineEditorBuffer>? onBufferDeactivated = null,
        Func<string, int, string?>? signatureHintProvider = null,
        bool shiftEnterExecutes = true,
        bool continuationGutterRightBorder = true,
        bool continuationLineNumbers = false)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(continuationPrompt);

        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        var theme = completionTheme ?? DefaultCompletionTheme;
        var buffer = new LineEditorBuffer(NormalizeLineEndings(initialText));
        if (initialCursorIndex is not null)
        {
            buffer.SetCursor(initialCursorIndex.Value);
        }

        var historyNavigator = new LineEditorHistory(history);
        var pendingKeys = new Queue<ConsoleKeyInfo>();
        var cursorRow = 1;
        int? preferredColumn = null;
        LineEditorCompletionState? completionState = null;
        LineEditorHistorySearchState? historySearchState = null;

        onBufferActivated?.Invoke(buffer);

        try
        {
            var initialHint = signatureHintProvider?.Invoke(buffer.Text, buffer.CursorIndex);
            cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, initialHint, continuationGutterRightBorder, continuationLineNumbers);

            while (true)
            {
                var key = ReadInputKey(pendingKeys);

                if (specialKeyHandler is not null && specialKeyHandler(buffer, key))
                {
                    completionState = null;
                    historySearchState = null;
                    preferredColumn = null;
                    cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                    continue;
                }

                if (completionState is not null)
                {
                    if (TryHandleCompletionPickerKey(buffer, key, ref completionState))
                    {
                        cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                        continue;
                    }
                }

                if (historySearchState is not null)
                {
                    if (TryHandleHistorySearchKey(buffer, key, ref historySearchState, out var shouldSubmit))
                    {
                        completionState = null;
                        preferredColumn = null;
                        cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);

                        if (shouldSubmit)
                        {
                            Console.WriteLine();
                            return buffer.Text;
                        }

                        continue;
                    }

                    historySearchState = null;
                }

                if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.R)
                {
                    historySearchState = new LineEditorHistorySearchState(history, buffer.Text, buffer.CursorIndex);
                    historySearchState.Activate(buffer);
                    completionState = null;
                    preferredColumn = null;
                    cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                    continue;
                }


                // Handle Ctrl+D (EOF)
                if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.D && buffer.Text.Length == 0)
                {
                    Console.WriteLine();
                    return null;
                }

                // Handle Ctrl+C (SIGINT)
                if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C)
                {
                    Console.WriteLine();
                    throw new ReplInterruptException();
                }

                if (shiftEnterExecutes && (IsExecuteEnterKey(key) || IsExecuteFallbackKey(key)))
                {
                    buffer.SetCursor(buffer.Text.Length);
                    completionState = null;
                    cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                    Console.WriteLine();
                    return buffer.Text;
                }

                if (!shiftEnterExecutes && key.Key == ConsoleKey.Enter && key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    var continuationState = continuationHandler?.Invoke(buffer.Text) ?? default;
                    InsertRequestedNewLine(buffer, BuildInsertedNewLineText(buffer.Text, buffer.CursorIndex, continuationState));
                    preferredColumn = null;
                    completionState = null;
                    cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (shiftEnterExecutes)
                    {
                        var continuationState = continuationHandler?.Invoke(buffer.Text) ?? default;

                        if (ShouldInsertNewLineOnEnter(buffer.Text, continuationState))
                        {
                            InsertRequestedNewLine(buffer, BuildInsertedNewLineText(buffer.Text, buffer.CursorIndex, continuationState));
                            preferredColumn = null;
                            completionState = null;
                            cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                            continue;
                        }

                        buffer.SetCursor(buffer.Text.Length);
                        completionState = null;
                        cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                        Console.WriteLine();
                        return buffer.Text;
                    }

                    var legacyContinuationState = continuationHandler?.Invoke(buffer.Text) ?? default;

                    if (legacyContinuationState.RequiresContinuation)
                    {
                        InsertRequestedNewLine(buffer, "\n" + legacyContinuationState.SuggestedIndent);
                        preferredColumn = null;
                        completionState = null;
                        cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                        continue;
                    }

                    buffer.SetCursor(buffer.Text.Length);
                    completionState = null;
                    cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, gutterRightBorder: continuationGutterRightBorder, continuationLineNumbers: continuationLineNumbers);
                    Console.WriteLine();
                    return buffer.Text;
                }

                var shouldRender = false;

                if (key.Key == ConsoleKey.Tab && completionProvider is not null)
                {
                    shouldRender = ApplyCompletion(buffer, completionProvider, ref completionState, reverse: key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                }
                else
                {
                    shouldRender = HandleKey(buffer, historyNavigator, prompt, continuationPrompt, key, ref preferredColumn);

                    if (shouldRender)
                    {
                        completionState = null;
                    }
                }

                if (shouldRender)
                {
                    var hint = signatureHintProvider?.Invoke(buffer.Text, buffer.CursorIndex);
                    cursorRow = Render(prompt, continuationPrompt, buffer, cursorRow, completionState, historySearchState, highlighter, maxVisibleSuggestions, showGhostText, theme, hint, continuationGutterRightBorder, continuationLineNumbers);
                }
            }
        }
        finally
        {
            onBufferDeactivated?.Invoke(buffer);
        }
    }

    private static ConsoleKeyInfo ReadInputKey(Queue<ConsoleKeyInfo> pendingKeys)
    {
        ArgumentNullException.ThrowIfNull(pendingKeys);

        if (pendingKeys.Count > 0)
        {
            return pendingKeys.Dequeue();
        }

        var key = Console.ReadKey(intercept: true);

        if (key.Key != ConsoleKey.Escape)
        {
            return key;
        }

        var trailingKeys = ReadPendingEscapeSequenceKeys();

        if (trailingKeys.Count == 0)
        {
            return key;
        }

        var sequence = new string(trailingKeys.Select(static trailingKey => trailingKey.KeyChar).ToArray());

        if (TryTranslateEscapeSequence(sequence, out var translated))
        {
            return translated;
        }

        foreach (var trailingKey in trailingKeys)
        {
            pendingKeys.Enqueue(trailingKey);
        }

        return key;
    }

    private static List<ConsoleKeyInfo> ReadPendingEscapeSequenceKeys()
    {
        var trailingKeys = new List<ConsoleKeyInfo>();

        if (!WaitForPendingConsoleKey(maxWaitMilliseconds: 8))
        {
            return trailingKeys;
        }

        do
        {
            trailingKeys.Add(Console.ReadKey(intercept: true));
        }
        while (trailingKeys.Count < 8 && WaitForPendingConsoleKey(maxWaitMilliseconds: 2));

        return trailingKeys;
    }

    private static bool WaitForPendingConsoleKey(int maxWaitMilliseconds)
    {
        if (Console.KeyAvailable)
        {
            return true;
        }

        var deadline = Environment.TickCount64 + Math.Max(0, maxWaitMilliseconds);

        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);

            if (Console.KeyAvailable)
            {
                return true;
            }
        }

        return Console.KeyAvailable;
    }

    internal static bool TryTranslateEscapeSequence(string sequence, out ConsoleKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        switch (sequence)
        {
            case "[1;3A":
                key = new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: false, alt: true, control: false);
                return true;
            case "[1;3B":
                key = new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: true, control: false);
                return true;
            case "[1;3C":
                key = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: true, control: false);
                return true;
            case "[1;3D":
                key = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: false, alt: true, control: false);
                return true;
            case "OP":
            case "[11~":
            case "[[A":
                key = new ConsoleKeyInfo('\0', ConsoleKey.F1, shift: false, alt: false, control: false);
                return true;
            case "OQ":
            case "[12~":
            case "[[B":
                key = new ConsoleKeyInfo('\0', ConsoleKey.F2, shift: false, alt: false, control: false);
                return true;
            case "OR":
            case "[13~":
            case "[[C":
                key = new ConsoleKeyInfo('\0', ConsoleKey.F3, shift: false, alt: false, control: false);
                return true;
            case "OS":
            case "[14~":
            case "[[D":
                key = new ConsoleKeyInfo('\0', ConsoleKey.F4, shift: false, alt: false, control: false);
                return true;
            case "h":
                key = new ConsoleKeyInfo('h', ConsoleKey.H, shift: false, alt: true, control: false);
                return true;
            case "H":
                key = new ConsoleKeyInfo('H', ConsoleKey.H, shift: true, alt: true, control: false);
                return true;
            case "i":
                key = new ConsoleKeyInfo('i', ConsoleKey.I, shift: false, alt: true, control: false);
                return true;
            case "I":
                key = new ConsoleKeyInfo('I', ConsoleKey.I, shift: true, alt: true, control: false);
                return true;
            default:
                key = default;
                return false;
        }
    }

    private static bool TryHandleHistorySearchKey(
        LineEditorBuffer buffer,
        ConsoleKeyInfo key,
        ref LineEditorHistorySearchState? historySearchState,
        out bool shouldSubmit)
    {
        shouldSubmit = false;

        if (historySearchState is null)
        {
            return false;
        }

        if (key.Modifiers == ConsoleModifiers.Control)
        {
            switch (key.Key)
            {
                case ConsoleKey.R:
                    historySearchState.TryCyclePrevious(buffer);
                    return true;
                case ConsoleKey.G:
                    historySearchState.Cancel(buffer);
                    historySearchState = null;
                    return true;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                buffer.SetCursor(buffer.Text.Length);
                historySearchState = null;
                shouldSubmit = true;
                return true;
            case ConsoleKey.Escape:
                historySearchState.Cancel(buffer);
                historySearchState = null;
                return true;
            case ConsoleKey.Backspace:
                if (!historySearchState.Backspace(buffer))
                {
                    historySearchState.Cancel(buffer);
                    historySearchState = null;
                }

                return true;
        }

        if (!char.IsControl(key.KeyChar))
        {
            historySearchState.Append(buffer, key.KeyChar);
            return true;
        }

        return false;
    }

    internal static bool ShouldInsertNewLineOnEnter(string text, ReplContinuationState continuationState)
    {
        if (continuationState.RequiresContinuation)
        {
            return true;
        }

        return text.Contains('\n');
    }

    internal static bool IsExecuteEnterKey(ConsoleKeyInfo key)
    {
        if (key.Key != ConsoleKey.Enter)
        {
            return false;
        }

        // Some terminals do not preserve Shift on Enter, but emit '\n' instead of '\r'.
        return key.Modifiers.HasFlag(ConsoleModifiers.Shift) || key.KeyChar == '\n';
    }

    internal static bool IsExecuteFallbackKey(ConsoleKeyInfo key)
    {
        // Reliable fallback for terminals that collapse Shift+Enter to plain Enter.
        return key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.J;
    }

    private static bool HandleKey(
        LineEditorBuffer buffer,
        LineEditorHistory historyNavigator,
        string prompt,
        string continuationPrompt,
        ConsoleKeyInfo key,
        ref int? preferredColumn)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    return historyNavigator.TryPrevious(buffer.Text, out var previousAlt) && ApplyHistory(buffer, previousAlt, ref preferredColumn);
                case ConsoleKey.DownArrow:
                    return historyNavigator.TryNext(out var nextAlt) && ApplyHistory(buffer, nextAlt, ref preferredColumn);
                case ConsoleKey.Z:
                    return ResetPreferredColumn(buffer.Undo(), ref preferredColumn);
                case ConsoleKey.Y:
                    return ResetPreferredColumn(buffer.Redo(), ref preferredColumn);
            }
        }

        if (key.Modifiers == ConsoleModifiers.Control)
        {
            // Ctrl+Z may be intercepted by terminal job control on POSIX before it reaches
            // ReadKey. Support Ctrl+_ (ASCII Unit Separator) as a reliable undo fallback.
            if (key.KeyChar == '\u001F' ||
                key.Key == ConsoleKey.Z ||
                key.KeyChar == '\u001A' ||
                key.Key == ConsoleKey.Oem2 ||
                key.Key == ConsoleKey.Divide ||
                key.Key == ConsoleKey.OemMinus ||
                key.Key == ConsoleKey.Subtract)
            {
                return ResetPreferredColumn(buffer.Undo(), ref preferredColumn);
            }

            if (key.Key == ConsoleKey.Y || key.KeyChar == '\u0019')
            {
                return ResetPreferredColumn(buffer.Redo(), ref preferredColumn);
            }

            return key.Key switch
            {
                ConsoleKey.A => ResetPreferredColumn(MoveLogicalLineHome(buffer), ref preferredColumn),
                ConsoleKey.E => ResetPreferredColumn(MoveLogicalLineEnd(buffer), ref preferredColumn),
                ConsoleKey.U => ResetPreferredColumn(buffer.Clear(), ref preferredColumn),
                ConsoleKey.W => ResetPreferredColumn(buffer.DeleteWordBackward(), ref preferredColumn),
                ConsoleKey.K => ResetPreferredColumn(buffer.KillToEnd(), ref preferredColumn),
                ConsoleKey.LeftArrow => ResetPreferredColumn(buffer.MoveWordLeft(), ref preferredColumn),
                ConsoleKey.RightArrow => ResetPreferredColumn(buffer.MoveWordRight(), ref preferredColumn),
                ConsoleKey.L => ClearScreen(),
                _ => false,
            };
        }

        return key.Key switch
        {
            ConsoleKey.Backspace => ResetPreferredColumn(buffer.Backspace(), ref preferredColumn),
            ConsoleKey.Delete => ResetPreferredColumn(buffer.Delete(), ref preferredColumn),
            ConsoleKey.LeftArrow => ResetPreferredColumn(buffer.MoveLeft(), ref preferredColumn),
            ConsoleKey.RightArrow => ResetPreferredColumn(buffer.MoveRight(), ref preferredColumn),
            ConsoleKey.Home => ResetPreferredColumn(MoveLogicalLineHome(buffer), ref preferredColumn),
            ConsoleKey.End => ResetPreferredColumn(MoveLogicalLineEnd(buffer), ref preferredColumn),
            ConsoleKey.UpArrow => HandleUpArrow(buffer, historyNavigator, prompt, continuationPrompt, ref preferredColumn),
            ConsoleKey.DownArrow => HandleDownArrow(buffer, historyNavigator, prompt, continuationPrompt, ref preferredColumn),
            _ => HandleCharacterInput(buffer, key, ref preferredColumn),
        };
    }

    internal static bool MoveLogicalLineHome(LineEditorBuffer buffer)
    {
        var target = FindLogicalLineStart(buffer.Text, buffer.CursorIndex);

        if (target == buffer.CursorIndex)
        {
            return false;
        }

        buffer.SetCursor(target);
        return true;
    }

    internal static bool MoveLogicalLineEnd(LineEditorBuffer buffer)
    {
        var target = FindLogicalLineEnd(buffer.Text, buffer.CursorIndex);

        if (target == buffer.CursorIndex)
        {
            return false;
        }

        buffer.SetCursor(target);
        return true;
    }

    internal static int FindLogicalLineStart(string text, int cursorIndex)
    {
        ArgumentNullException.ThrowIfNull(text);

        var index = Math.Clamp(cursorIndex, 0, text.Length);

        while (index > 0 && text[index - 1] != '\n')
        {
            index--;
        }

        return index;
    }

    internal static int FindLogicalLineEnd(string text, int cursorIndex)
    {
        ArgumentNullException.ThrowIfNull(text);

        var index = Math.Clamp(cursorIndex, 0, text.Length);

        while (index < text.Length && text[index] != '\n')
        {
            index++;
        }

        return index;
    }

    private static bool ApplyHistory(LineEditorBuffer buffer, string text, ref int? preferredColumn)
    {
        buffer.SetText(NormalizeLineEndings(text));
        preferredColumn = null;
        return true;
    }

    private static bool HandleCharacterInput(LineEditorBuffer buffer, ConsoleKeyInfo key, ref int? preferredColumn)
    {
        if (!char.IsControl(key.KeyChar))
        {
            return ResetPreferredColumn(buffer.Insert(key.KeyChar), ref preferredColumn);
        }

        return false;
    }

    private static bool HandleUpArrow(
        LineEditorBuffer buffer,
        LineEditorHistory historyNavigator,
        string prompt,
        string continuationPrompt,
        ref int? preferredColumn)
    {
        if (buffer.Text.Contains('\n') && IsAtFirstLogicalLine(buffer.Text, buffer.CursorIndex))
        {
            return historyNavigator.TryPrevious(buffer.Text, out var historyPrevious) && ApplyHistory(buffer, historyPrevious, ref preferredColumn);
        }

        if (TryMoveWrappedVertical(buffer, prompt, continuationPrompt, GetConsoleWidth(), -1, preferredColumn, out var actualColumn))
        {
            preferredColumn = actualColumn;
            return true;
        }

        return historyNavigator.TryPrevious(buffer.Text, out var previous) && ApplyHistory(buffer, previous, ref preferredColumn);
    }

    private static bool HandleDownArrow(
        LineEditorBuffer buffer,
        LineEditorHistory historyNavigator,
        string prompt,
        string continuationPrompt,
        ref int? preferredColumn)
    {
        if (buffer.Text.Contains('\n') && IsAtLastLogicalLine(buffer.Text, buffer.CursorIndex))
        {
            return historyNavigator.TryNext(out var historyNext) && ApplyHistory(buffer, historyNext, ref preferredColumn);
        }

        if (TryMoveWrappedVertical(buffer, prompt, continuationPrompt, GetConsoleWidth(), 1, preferredColumn, out var actualColumn))
        {
            preferredColumn = actualColumn;
            return true;
        }

        return historyNavigator.TryNext(out var next) && ApplyHistory(buffer, next, ref preferredColumn);
    }

    private static bool ResetPreferredColumn(bool result, ref int? preferredColumn)
    {
        if (result)
        {
            preferredColumn = null;
        }

        return result;
    }

    internal static bool TryMoveWrappedVertical(
        LineEditorBuffer buffer,
        string prompt,
        string continuationPrompt,
        int consoleWidth,
        int direction,
        int? preferredColumn,
        out int actualColumn)
    {
        actualColumn = 0;

        if (consoleWidth <= 0 || direction == 0)
        {
            return false;
        }

        var layout = BuildInputLayout(prompt, continuationPrompt, buffer.Text, buffer.Text, consoleWidth);
        var currentPosition = layout.CursorPositions[buffer.CursorIndex];
        var targetRow = currentPosition.Row + direction;

        if (targetRow < 1)
        {
            return false;
        }

        var desiredColumn = preferredColumn ?? currentPosition.Column;
        var bestIndex = FindClosestCursorIndex(layout.CursorPositions, targetRow, desiredColumn);

        if (bestIndex < 0 || bestIndex == buffer.CursorIndex)
        {
            return false;
        }

        buffer.SetCursor(bestIndex);
        actualColumn = layout.CursorPositions[bestIndex].Column;
        return true;
    }

    internal static RenderLayout BuildInputLayout(
        string prompt,
        string continuationPrompt,
        string rawInput,
        string highlightedInput,
        int consoleWidth,
        bool gutterRightBorder = true,
        bool continuationLineNumbers = false)
    {
        var normalizedRaw = NormalizeLineEndings(rawInput);
        var normalizedHighlighted = NormalizeLineEndings(highlightedInput);
        var rawLines = SplitLinesPreserveEmpty(normalizedRaw);
        var highlightedLines = SplitLinesPreserveEmpty(normalizedHighlighted);
        var builder = new StringBuilder();
        var cursorPositions = new VisualPosition[normalizedRaw.Length + 1];
        var normalizedContinuationPrompt = NormalizeContinuationPromptWidth(prompt, continuationPrompt, consoleWidth);
        var dynamicContinuationGutters = BuildDynamicContinuationGutters(rawLines, prompt, continuationPrompt, consoleWidth, gutterRightBorder, continuationLineNumbers);
        var row = 1;
        var column = 0;
        var textIndex = 0;

        for (var lineIndex = 0; lineIndex < rawLines.Count; lineIndex++)
        {
            if (lineIndex > 0)
            {
                builder.AppendLine();
                row++;
                column = 0;
                textIndex++;
            }

            var promptText = lineIndex == 0
                ? prompt
                : dynamicContinuationGutters[lineIndex] ?? normalizedContinuationPrompt;
            builder.Append(promptText);
            ConsumeDisplayText(AnsiEscapePattern.Replace(promptText, string.Empty), consoleWidth, ref row, ref column);

            cursorPositions[textIndex] = new VisualPosition(row, column);

            var rawLine = rawLines[lineIndex];
            var highlightedLine = lineIndex < highlightedLines.Count ? highlightedLines[lineIndex] : rawLine;
            builder.Append(highlightedLine);

            foreach (var character in rawLine)
            {
                ConsumeVisibleCharacter(character, consoleWidth, ref row, ref column);
                textIndex++;
                cursorPositions[textIndex] = new VisualPosition(row, column);
            }
        }

        return new RenderLayout(builder.ToString(), cursorPositions);
    }

    internal static string NormalizeContinuationPromptWidth(string prompt, string continuationPrompt, int consoleWidth)
    {
        var promptVisibleWidth = GetPromptInputColumn(prompt, consoleWidth);
        var continuationVisibleWidth = GetVisibleWidth(continuationPrompt);

        if (continuationVisibleWidth == promptVisibleWidth)
        {
            return continuationPrompt;
        }

        if (continuationVisibleWidth < promptVisibleWidth)
        {
            return continuationPrompt + new string(' ', promptVisibleWidth - continuationVisibleWidth);
        }

        // Continuation prompt is wider than prompt: trim so multiline input keeps the
        // same left margin as the primary prompt line.
        return ClipToVisibleWidth(continuationPrompt, promptVisibleWidth);
    }

    internal static IReadOnlyList<string> BuildDynamicContinuationGutters(IReadOnlyList<string> rawLines, string prompt, string continuationPrompt, int consoleWidth, bool gutterRightBorder = true, bool continuationLineNumbers = false)
    {
        var promptWidth = GetPromptInputColumn(prompt, consoleWidth);
        var fallback = NormalizeContinuationPromptWidth(prompt, continuationPrompt, consoleWidth);
        var glyphs = ResolveGutterGlyphs();

        if (rawLines.Count <= 1)
        {
            return Array.Empty<string>();
        }

        var gutters = new string[rawLines.Count];
        var depth = 0;

        for (var i = 0; i < rawLines.Count; i++)
        {
            var line = rawLines[i] ?? string.Empty;
            var previousLine = i > 0 ? rawLines[i - 1] ?? string.Empty : string.Empty;

            if (i == 1)
            {
                gutters[i] = BuildDepthGutter(promptWidth, GutterMarker.Vertical, depth: 1, fallback, glyphs, lineNumber: i + 1, gutterRightBorder, continuationLineNumbers);
                depth = Math.Max(0, depth + ComputeBraceDelta(line));
                continue;
            }

            var startsWithCloser = StartsWithCloserToken(line);
            var endsWithOpener = EndsWithOpenerToken(line);
            var previousEndsWithOpener = EndsWithOpenerToken(previousLine);

            var effectiveDepth = Math.Max(0, depth - (startsWithCloser ? 1 : 0));
            if (previousEndsWithOpener && !startsWithCloser)
            {
                effectiveDepth = Math.Max(1, effectiveDepth);
            }

            var marker = SelectGutterMarker(startsWithCloser, endsWithOpener, effectiveDepth);
            gutters[i] = BuildDepthGutter(promptWidth, marker, effectiveDepth, fallback, glyphs, lineNumber: i + 1, gutterRightBorder, continuationLineNumbers);

            depth = Math.Max(0, depth + ComputeBraceDelta(line));
        }

        return gutters;
    }

    private static bool StartsWithCloserToken(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '}';
    }

    private static bool EndsWithOpenerToken(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith("{", StringComparison.Ordinal);
    }

    private static int ComputeBraceDelta(string line)
    {
        var delta = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaping = false;
        var inComment = false;

        foreach (var ch in line)
        {
            if (inComment)
            {
                continue;
            }

            if (inSingleQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (ch == '\'')
                {
                    inSingleQuote = false;
                }

                continue;
            }

            if (inDoubleQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (ch == '"')
                {
                    inDoubleQuote = false;
                }

                continue;
            }

            switch (ch)
            {
                case '#':
                    inComment = true;
                    break;
                case '\'':
                    inSingleQuote = true;
                    break;
                case '"':
                    inDoubleQuote = true;
                    break;
                case '{':
                    delta++;
                    break;
                case '}':
                    delta--;
                    break;
            }
        }

        return delta;
    }

    private static GutterMarker SelectGutterMarker(bool startsWithCloser, bool endsWithOpener, int depth)
    {
        // Transition lines like `} catch {` / `} else {` are easier to follow when
        // rendered as a bridge marker at the current depth.
        if (startsWithCloser && endsWithOpener)
        {
            return GutterMarker.Transition;
        }

        if (startsWithCloser)
        {
            return GutterMarker.Close;
        }

        if (endsWithOpener)
        {
            return GutterMarker.Open;
        }

        if (depth > 0)
        {
            return GutterMarker.Vertical;
        }

        return GutterMarker.Dot;
    }

    private static string BuildDepthGutter(int width, GutterMarker marker, int depth, string fallback, GutterGlyphs glyphs, int lineNumber, bool gutterRightBorder, bool continuationLineNumbers)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var chars = new string(' ', width).ToCharArray();

        var bars = marker == GutterMarker.Vertical
            ? Math.Clamp(depth - 1, 0, Math.Max(0, width - 1))
            : Math.Clamp(depth, 0, Math.Max(0, width - 1));

        for (var i = 0; i < bars; i++)
        {
            chars[i] = glyphs.Vertical;
        }

        var markerColumn = marker == GutterMarker.Vertical
            ? Math.Clamp(depth - 1, 0, width - 1)
            : Math.Min(bars, width - 1);

        if (marker == GutterMarker.Dot)
        {
            markerColumn = 0;
        }

        chars[markerColumn] = marker switch
        {
            GutterMarker.Open => glyphs.Open,
            GutterMarker.Close => glyphs.Close,
            GutterMarker.Transition => glyphs.Transition,
            GutterMarker.Dot => glyphs.Dot,
            _ => glyphs.Vertical,
        };

        // For nested open/close transitions, render a join to avoid abrupt vertical->corner jumps.
        if ((marker is GutterMarker.Open or GutterMarker.Close or GutterMarker.Transition) && depth > 0)
        {
            var joinColumn = Math.Clamp(depth - 1, 0, width - 1);
            chars[joinColumn] = glyphs.Join;
        }

        if (continuationLineNumbers)
        {
            var lineNumberText = lineNumber.ToString();
            var rightmostNumberColumn = gutterRightBorder ? width - 2 : width - 1;

            if (rightmostNumberColumn >= 0)
            {
                var startColumn = Math.Max(0, rightmostNumberColumn - lineNumberText.Length + 1);
                var targetColumn = startColumn;

                foreach (var digit in lineNumberText)
                {
                    if (targetColumn > rightmostNumberColumn)
                    {
                        break;
                    }

                    chars[targetColumn++] = digit;
                }
            }
        }

        if (gutterRightBorder && width > 0)
        {
            chars[width - 1] = glyphs.Vertical;
        }

        var gutter = new string(chars);
        return gutter.Length == width ? gutter : fallback;
    }

    internal static GutterGlyphs ResolveGutterGlyphs()
    {
        var resolvedStyle = TerminalGlyphs.ResolveBoxStyle(ToshTableBoxStyle.Rounded);

        return resolvedStyle switch
        {
            ToshTableBoxStyle.Ascii => new GutterGlyphs(Vertical: '|', Open: '+', Close: '+', Join: '+', Transition: '+', Dot: '.'),
            ToshTableBoxStyle.Square => new GutterGlyphs(Vertical: '│', Open: '┐', Close: '┘', Join: '├', Transition: '┤', Dot: '.'),
            ToshTableBoxStyle.Heavy => new GutterGlyphs(Vertical: '┃', Open: '┓', Close: '┛', Join: '┣', Transition: '┫', Dot: '·'),
            ToshTableBoxStyle.Double => new GutterGlyphs(Vertical: '║', Open: '╗', Close: '╝', Join: '╠', Transition: '╣', Dot: '·'),
            _ => new GutterGlyphs(Vertical: '│', Open: '╮', Close: '╯', Join: '├', Transition: '┤', Dot: '·'),
        };
    }

    internal readonly record struct GutterGlyphs(char Vertical, char Open, char Close, char Join, char Transition, char Dot);

    private enum GutterMarker
    {
        Dot,
        Vertical,
        Open,
        Close,
        Transition,
    }

    private static int GetVisibleWidth(string text)
    {
        var stripped = AnsiEscapePattern.Replace(text ?? string.Empty, string.Empty);
        return stripped.Length;
    }

    private static int GetPromptInputColumn(string prompt, int consoleWidth)
    {
        var normalizedPrompt = NormalizeLineEndings(prompt ?? string.Empty);
        var row = 1;
        var column = 0;
        ConsumeDisplayText(AnsiEscapePattern.Replace(normalizedPrompt, string.Empty), Math.Max(1, consoleWidth), ref row, ref column);
        return column;
    }

    private static string ClipToVisibleWidth(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var stripped = AnsiEscapePattern.Replace(text ?? string.Empty, string.Empty);
        return stripped.Length <= width ? stripped : stripped[..width];
    }

    private static int Render(
        string prompt,
        string continuationPrompt,
        LineEditorBuffer buffer,
        int previousCursorRow,
        LineEditorCompletionState? completionState,
        LineEditorHistorySearchState? historySearchState,
        Func<string, string>? highlighter,
        int maxVisibleSuggestions,
        bool showGhostText,
        ToshCompletionThemeConfig theme,
        string? signatureHint = null,
        bool gutterRightBorder = true,
        bool continuationLineNumbers = false)
    {
        var renderedInput = RenderHighlightedInput(buffer, completionState, highlighter, showGhostText, theme);
        var consoleWidth = GetConsoleWidth();
        var inputLayout = BuildInputLayout(prompt, continuationPrompt, buffer.Text, renderedInput, consoleWidth, gutterRightBorder, continuationLineNumbers);
        var overlay = historySearchState is not null
            ? BuildHistorySearchOverlay(historySearchState, theme)
            : BuildSuggestionOverlay(completionState, maxVisibleSuggestions, theme);

        if (overlay.Length == 0 && signatureHint is { Length: > 0 })
        {
            overlay = "\n" + theme.Detail.Apply(signatureHint).ToAnsi();
        }

        var renderedText = inputLayout.RenderedText + overlay;
        var totalRows = CalculateRenderedRows(renderedText, consoleWidth);
        var cursorPosition = inputLayout.CursorPositions[buffer.CursorIndex];

        MoveToPromptStart(previousCursorRow);
        Console.Write(ClearToEndOfScreen);
        Console.Write(renderedText);

        var rowsFromEnd = totalRows - cursorPosition.Row;

        if (rowsFromEnd > 0)
        {
            Console.Write($"\u001b[{rowsFromEnd}A");
        }

        Console.Write("\r");

        if (cursorPosition.Column > 0)
        {
            Console.Write($"\u001b[{cursorPosition.Column}C");
        }

        return cursorPosition.Row;
    }

    private static string BuildHistorySearchOverlay(LineEditorHistorySearchState state, ToshCompletionThemeConfig theme)
    {
        var builder = new StringBuilder();
        var label = state.Failed ? "(failed reverse-i-search) " : "(reverse-i-search) ";
        var preview = state.HasMatch && state.MatchText is not null
            ? FormatHistoryPreview(state.MatchText)
            : string.Empty;

        builder.AppendLine();
        builder.Append(theme.Header.Apply(label).ToAnsi());
        builder.Append(theme.SelectedLabel.Apply($"'{state.Query}'").ToAnsi());
        builder.Append(theme.Header.Apply(": ").ToAnsi());
        builder.Append(theme.Item.Apply(preview).ToAnsi());
        builder.AppendLine();
        builder.Append(theme.Footer.Apply("    Ctrl+R previous  Enter accept  Esc/Ctrl+G cancel").ToAnsi());
        return builder.ToString();
    }

    internal static string FormatHistoryPreview(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var preview = NormalizeLineEndings(text).Replace('\n', ' ');

        if (preview.Length <= 72)
        {
            return preview;
        }

        return preview[..69] + "...";
    }

    private static string RenderHighlightedInput(
        LineEditorBuffer buffer,
        LineEditorCompletionState? completionState,
        Func<string, string>? highlighter,
        bool showGhostText,
        ToshCompletionThemeConfig theme)
    {
        var applyHighlighting = highlighter ?? (text => text);

        if (completionState is null || !showGhostText)
        {
            return applyHighlighting(buffer.Text);
        }

        var ghostText = BuildGhostText(buffer, completionState, theme);

        if (ghostText.Length == 0)
        {
            return applyHighlighting(buffer.Text);
        }

        var beforeCursor = buffer.Text[..buffer.CursorIndex];
        var afterCursor = buffer.Text[buffer.CursorIndex..];
        return applyHighlighting(beforeCursor) + ghostText + applyHighlighting(afterCursor);
    }

    private static string BuildGhostText(LineEditorBuffer buffer, LineEditorCompletionState state, ToshCompletionThemeConfig theme)
    {
        var replacementEnd = state.ReplacementStart + state.ReplacementLength;

        if (buffer.CursorIndex != replacementEnd)
        {
            return string.Empty;
        }

        var typedText = state.BaseText.Substring(state.ReplacementStart, state.ReplacementLength);
        var suggestion = state.Suggestions[state.SelectedIndex].Label;

        if (!suggestion.StartsWith(typedText, StringComparison.OrdinalIgnoreCase) ||
            suggestion.Length <= typedText.Length)
        {
            return string.Empty;
        }

        return theme.GhostText.Apply(suggestion[typedText.Length..]).ToAnsi();
    }

    private static string BuildSuggestionOverlay(LineEditorCompletionState? state, int maxVisible, ToshCompletionThemeConfig theme)
    {
        if (state is null || state.Suggestions.Count == 0)
        {
            return string.Empty;
        }

        maxVisible = Math.Max(1, maxVisible);
        var windowStart = Math.Clamp(state.SelectedIndex - (maxVisible / 2), 0, Math.Max(0, state.Suggestions.Count - maxVisible));
        var visibleSuggestions = state.Suggestions.Skip(windowStart).Take(maxVisible).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.Append(theme.Header.Apply("completions").ToAnsi());

        for (var index = 0; index < visibleSuggestions.Length; index++)
        {
            var suggestion = visibleSuggestions[index];
            var suggestionIndex = windowStart + index;

            builder.AppendLine();
            builder.Append((suggestionIndex == state.SelectedIndex ? theme.SelectedPointer : theme.Item)
                .Apply(suggestionIndex == state.SelectedIndex ? "  > " : "    ")
                .ToAnsi());
            builder.Append((suggestionIndex == state.SelectedIndex ? theme.SelectedLabel : theme.Item)
                .Apply(suggestion.Label)
                .ToAnsi());

            if (!string.IsNullOrWhiteSpace(suggestion.Detail))
            {
                builder.Append(theme.Detail.Apply("  " + suggestion.Detail).ToAnsi());
            }
        }

        if (windowStart > 0 || windowStart + visibleSuggestions.Length < state.Suggestions.Count)
        {
            builder.AppendLine();
            builder.Append(theme.Footer.Apply($"    showing {windowStart + 1}-{windowStart + visibleSuggestions.Length} of {state.Suggestions.Count}").ToAnsi());
        }

        builder.AppendLine();
        builder.Append(theme.Footer.Apply("    ↑/↓ navigate  Tab/Enter accept  Esc/q cancel").ToAnsi());
        return builder.ToString();
    }

    private static void MoveToPromptStart(int cursorRow)
    {
        if (cursorRow > 1)
        {
            Console.Write($"\u001b[{cursorRow - 1}A");
        }

        Console.Write("\r");
    }

    private static int CalculateRenderedRows(string renderedText, int consoleWidth)
    {
        var stripped = AnsiEscapePattern.Replace(renderedText, string.Empty);
        var lines = stripped.Split('\n');
        var rows = 0;

        foreach (var line in lines)
        {
            var lineLength = line.TrimEnd('\r').Length;
            rows += lineLength > 0 ? ((lineLength - 1) / consoleWidth) + 1 : 1;
        }

        return rows;
    }

    internal static string BuildInsertedNewLineText(string text, int cursorIndex, ReplContinuationState continuationState)
    {
        if (continuationState.RequiresContinuation)
        {
            return "\n" + continuationState.SuggestedIndent;
        }

        return "\n" + GetCurrentLineIndent(text, cursorIndex);
    }

    private static void InsertRequestedNewLine(LineEditorBuffer buffer, string insertedText)
    {
        buffer.Insert(insertedText);
    }

    internal static string GetCurrentLineIndent(string text, int cursorIndex)
    {
        var normalized = NormalizeLineEndings(text);
        var clampedIndex = Math.Clamp(cursorIndex, 0, normalized.Length);
        var lineStart = normalized.LastIndexOf('\n', Math.Max(0, clampedIndex - 1));
        var sliceStart = lineStart >= 0 ? lineStart + 1 : 0;
        var sliceLength = clampedIndex - sliceStart;
        var lineText = sliceLength > 0 ? normalized.Substring(sliceStart, sliceLength) : string.Empty;
        var indentLength = 0;

        while (indentLength < lineText.Length && lineText[indentLength] is ' ' or '\t')
        {
            indentLength++;
        }

        return lineText[..indentLength];
    }

    internal static bool IsAtFirstLogicalLine(string text, int cursorIndex)
    {
        var normalized = NormalizeLineEndings(text);
        var clampedIndex = Math.Clamp(cursorIndex, 0, normalized.Length);
        return normalized.LastIndexOf('\n', Math.Max(0, clampedIndex - 1)) < 0;
    }

    internal static bool IsAtLastLogicalLine(string text, int cursorIndex)
    {
        var normalized = NormalizeLineEndings(text);
        var clampedIndex = Math.Clamp(cursorIndex, 0, normalized.Length);
        return normalized.IndexOf('\n', clampedIndex) < 0;
    }

    private static int FindClosestCursorIndex(IReadOnlyList<VisualPosition> positions, int targetRow, int desiredColumn)
    {
        var bestIndex = -1;
        var bestDistance = int.MaxValue;
        var bestColumn = -1;

        for (var index = 0; index < positions.Count; index++)
        {
            var position = positions[index];

            if (position.Row != targetRow)
            {
                continue;
            }

            var distance = Math.Abs(position.Column - desiredColumn);

            if (distance < bestDistance ||
                (distance == bestDistance && position.Column > bestColumn && position.Column <= desiredColumn) ||
                (distance == bestDistance && bestIndex < 0))
            {
                bestIndex = index;
                bestDistance = distance;
                bestColumn = position.Column;
            }
        }

        return bestIndex;
    }

    private static void ConsumeDisplayText(string text, int consoleWidth, ref int row, ref int column)
    {
        foreach (var character in text)
        {
            if (character == '\n')
            {
                row++;
                column = 0;
                continue;
            }

            ConsumeVisibleCharacter(character, consoleWidth, ref row, ref column);
        }
    }

    private static void ConsumeVisibleCharacter(char _, int consoleWidth, ref int row, ref int column)
    {
        column++;

        if (column >= consoleWidth)
        {
            row++;
            column = 0;
        }
    }

    private static IReadOnlyList<string> SplitLinesPreserveEmpty(string text)
    {
        return text.Split('\n');
    }

    private static string NormalizeLineEndings(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
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

    private static bool ApplyCompletion(
        LineEditorBuffer buffer,
        Func<string, int, ReplCompletionResult?> completionProvider,
        ref LineEditorCompletionState? completionState,
        bool reverse)
    {
        if (completionState is not null)
        {
            var direction = reverse ? -1 : 1;
            completionState.SelectedIndex = (completionState.SelectedIndex + direction + completionState.Suggestions.Count) % completionState.Suggestions.Count;
            return true;
        }

        var result = completionProvider(buffer.Text, buffer.CursorIndex);

        if (result is null || result.Suggestions.Count == 0)
        {
            return false;
        }

        if (result.Suggestions.Count == 1)
        {
            buffer.ReplaceRange(result.ReplacementStart, result.ReplacementLength, result.Suggestions[0].GetInsertText());
            return true;
        }

        var selectedIndex = reverse ? result.Suggestions.Count - 1 : 0;
        completionState = new LineEditorCompletionState(
            buffer.Text,
            buffer.CursorIndex,
            result.ReplacementStart,
            result.ReplacementLength,
            result.Suggestions,
            selectedIndex);
        return true;
    }

    internal static void ApplyCompletionSuggestion(
        LineEditorBuffer buffer,
        string baseText,
        int replacementStart,
        int replacementLength,
        string suggestion,
        string suffix = "")
    {
        buffer.SetText(baseText);
        buffer.ReplaceRange(replacementStart, replacementLength, suggestion + suffix);
    }

    private static bool TryHandleCompletionPickerKey(LineEditorBuffer buffer, ConsoleKeyInfo key, ref LineEditorCompletionState? completionState)
    {
        if (completionState is null)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                completionState.SelectedIndex = (completionState.SelectedIndex - 1 + completionState.Suggestions.Count) % completionState.Suggestions.Count;
                return true;

            case ConsoleKey.DownArrow:
                completionState.SelectedIndex = (completionState.SelectedIndex + 1) % completionState.Suggestions.Count;
                return true;

            case ConsoleKey.Tab:
            case ConsoleKey.Enter:
                ApplyCompletionSuggestion(
                    buffer,
                    completionState.BaseText,
                    completionState.ReplacementStart,
                    completionState.ReplacementLength,
                    completionState.Suggestions[completionState.SelectedIndex].GetInsertText());
                completionState = null;
                return true;

            case ConsoleKey.Escape:
                completionState = null;
                return true;

            default:
                if (TryGetCompletionContinuationSuffix(key.KeyChar, out var suffix))
                {
                    ApplyCompletionSuggestion(
                        buffer,
                        completionState.BaseText,
                        completionState.ReplacementStart,
                        completionState.ReplacementLength,
                        completionState.Suggestions[completionState.SelectedIndex].GetInsertText(),
                        suffix.ToString());
                    completionState = null;
                    return true;
                }

                if (key.KeyChar is 'q' or 'Q')
                {
                    completionState = null;
                    return true;
                }

                completionState = null;
                return false;
        }
    }

    private static bool TryGetCompletionContinuationSuffix(char keyChar, out char suffix)
    {
        switch (keyChar)
        {
            case '.':
            case '(':
            case '[':
            case '<':
                suffix = keyChar;
                return true;

            default:
                suffix = default;
                return false;
        }
    }

    internal readonly record struct VisualPosition(int Row, int Column);
    internal readonly record struct RenderLayout(string RenderedText, IReadOnlyList<VisualPosition> CursorPositions);

    private sealed record LineEditorCompletionState(
        string BaseText,
        int BaseCursorIndex,
        int ReplacementStart,
        int ReplacementLength,
        IReadOnlyList<ReplCompletionSuggestion> Suggestions,
        int SelectedIndex)
    {
        public int SelectedIndex { get; set; } = SelectedIndex;
    }
}

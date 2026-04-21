using Tosh.Core;
using Tosh.Language;
using Tosh.Cli.Tui;
using System.Diagnostics;

namespace Tosh.Cli;

public sealed class ToshRepl
{
    private readonly DiagnosticRenderer _diagnostics;
    private readonly ReplCompletionEngine _completionEngine;
    private readonly ReplCommandLineInsertionSink _commandLineInsertion;
    private readonly ToshEngine _engine;
    private readonly ReplLineEditor _lineEditor;
    private readonly ToshRuntime _runtime;

    public ToshRepl(ToshEngine engine)
    {
        _engine = engine;
        _runtime = engine.Runtime;
        _diagnostics = new DiagnosticRenderer(_runtime.Config.Theme.Diagnostics);
        _lineEditor = new ReplLineEditor();
        _commandLineInsertion = new ReplCommandLineInsertionSink();
        _runtime.CommandLineInsertion = _commandLineInsertion;
        _completionEngine = new ReplCompletionEngine(_runtime);
    }

    public async Task RunAsync()
    {
        await PrintBannerAsync();

        string[]? cachedHistory = null;
        var lastHistoryCount = -1;

        while (true)
        {
            if (lastHistoryCount != _runtime.History.Count)
            {
                cachedHistory = _runtime.History.Select(entry => entry.Text).ToArray();
                lastHistoryCount = _runtime.History.Count;
            }

            var initialText = string.Empty;
            int? initialCursorIndex = null;
            if (_commandLineInsertion.TryConsume(out var pendingCommandLine))
            {
                initialText = pendingCommandLine.Text;
                initialCursorIndex = pendingCommandLine.CursorIndex;
            }


            string? source = null;
            try
            {
                source = _lineEditor.ReadLine(
                    BuildPrompt(),
                    cachedHistory ?? Array.Empty<string>(),
                    (text, cursor) => _completionEngine.GetCompletions(text, cursor),
                    initialText: initialText,
                    initialCursorIndex: initialCursorIndex,
                    highlighter: _runtime.Config.Repl.SyntaxHighlightingEnabled ? text => SyntaxHighlighter.Highlight(text, _runtime) : null,
                    continuationPrompt: _runtime.Config.Repl.ContinuationPrompt,
                    maxVisibleSuggestions: _runtime.Config.Repl.CompletionMaxVisible,
                    showGhostText: _runtime.Config.Repl.GhostTextEnabled,
                    completionTheme: _runtime.Config.Theme.Completion,
                    continuationHandler: ReplInputClassifier.GetContinuationState,
                    specialKeyHandler: TryHandleInlineToolShortcut,
                    onBufferActivated: buffer => _commandLineInsertion.ActivateBuffer(buffer),
                    onBufferDeactivated: buffer => _commandLineInsertion.DeactivateBuffer(buffer),
                    signatureHintProvider: (text, cursor) => _completionEngine.GetSignatureHint(text, cursor),
                    shiftEnterExecutes: _runtime.Config.Repl.ShiftEnterExecutes,
                    continuationGutterRightBorder: _runtime.Config.Repl.ContinuationGutterRightBorder,
                    continuationLineNumbers: _runtime.Config.Repl.ContinuationLineNumbers);
            }
            catch (ReplInterruptException)
            {
                // Simulate interrupt: print new prompt, skip execution
                await Console.Out.WriteLineAsync("^C");
                continue;
            }

            if (source is null)
            {
                break;
            }


            var trimmed = source.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            // Re-arm background job warning if the user typed something other than exit.
            if (!trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) && !trimmed.Equals("logout", StringComparison.OrdinalIgnoreCase))
            {
                _runtime.ExitWarningIssued = false;
            }

            try
            {
                var expansion = ReplHistoryExpander.Expand(source, _runtime.History.ToArray());

                if (expansion.Expanded)
                {
                    await Console.Out.WriteLineAsync(
                        StyledText.RenderSegments(
                        [
                            _runtime.Config.Theme.Completion.Footer.Apply(expansion.Text),
                        ]));
                    source = expansion.Text;
                }

                var historyEntry = _runtime.RecordHistory(source);
                var sourceName = historyEntry is not null
                    ? $"repl_entry #{historyEntry.Id}"
                    : "repl_entry transient";
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    await ExecuteAndPrintAsync(source, sourceName);
                }
                finally
                {
                    stopwatch.Stop();
                    _runtime.SetLastCommandDuration(stopwatch.Elapsed);
                }
            }
            catch (Exception exception)
            {
                await Console.Error.WriteLineAsync(_diagnostics.Render(exception));
            }

            if (_runtime.ExitRequested)
            {
                break;
            }
        }
    }

    private async Task ExecuteAndPrintAsync(string source, string sourceName)
    {
        var values = await _engine.ExecuteToListAsync(source, sourceName);

        try
        {
            if (TuiRequestDispatcher.TryHandle(values, _runtime, out var outcomeValues))
            {
                if (outcomeValues is { Count: > 0 })
                {
                    var rendered = _runtime.Display.RenderMany(outcomeValues, ConsoleDisplay.CreateRenderOptions(_runtime));
                    await ConsoleDisplay.WriteRenderedAsync(rendered, _runtime);
                }

                return;
            }

            var rendered2 = _runtime.Display.RenderMany(values, ConsoleDisplay.CreateRenderOptions(_runtime));
            await ConsoleDisplay.WriteRenderedAsync(rendered2, _runtime);
        }
        finally
        {
            _runtime.ClearDisplaySelections();
        }
    }

    private string BuildPrompt()
    {
        if (_runtime.Commands.TryGet("prompt", out _))
        {
            try
            {
                var results = _engine.ExecuteToListAsync("prompt", "<prompt>").GetAwaiter().GetResult();

                if (results.Count > 0)
                {
                    // If any result is a StyledText, render all segments together.
                    if (results.Any(r => r is StyledText))
                    {
                        return StyledText.RenderSegments(results);
                    }

                    // Legacy: plain string return.
                    if (results[0] is string promptText)
                    {
                        return promptText;
                    }
                }
            }
            catch
            {
                // Fall through to default prompt on any error.
            }
        }

        return ToshPromptRenderer.BuildDefaultPrompt(_runtime);
    }

    private bool TryHandleInlineToolShortcut(LineEditorBuffer buffer, ConsoleKeyInfo key)
    {
        var inlinePrompts = _runtime.InlinePrompts;

        if (inlinePrompts is null)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.F1:
            case ConsoleKey.H when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                {
                    var tokenSpan = ReplCompletionEngine.GetTokenSpanAtCursor(buffer.Text, buffer.CursorIndex);
                    var query = ReplCompletionEngine.GetInlineHelpQuery(buffer.Text, buffer.CursorIndex);
                    var topicName = string.IsNullOrWhiteSpace(query) ? null : HelpCatalog.ResolveTopic(_runtime, query)?.Name;
                    _commandLineInsertion.SetPendingReplacement(tokenSpan.Start, tokenSpan.Length);

                    try
                    {
                        inlinePrompts.BrowseHelp(query, topicName);
                    }
                    finally
                    {
                        _commandLineInsertion.ClearPendingReplacement();
                    }

                    return true;
                }

            case ConsoleKey.F2:
            case ConsoleKey.I when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                {
                    var tokenSpan = ReplCompletionEngine.GetInspectTargetSpanAtCursor(buffer.Text, buffer.CursorIndex);
                    var token = tokenSpan.Token;

                    if (!_completionEngine.TryResolveInspectableReference(token, out var value))
                    {
                        return false;
                    }

                    _commandLineInsertion.SetPendingReplacement(tokenSpan.Start, tokenSpan.Length);

                    try
                    {
                        inlinePrompts.Inspect(value, sourceExpression: ReplCompletionEngine.BuildInspectableSourceExpression(token, value));
                    }
                    finally
                    {
                        _commandLineInsertion.ClearPendingReplacement();
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    private static async Task PrintBannerAsync()
    {
        await Console.Out.WriteLineAsync("ToSh (ToastedShell)");
        await Console.Out.WriteLineAsync("NuShell inspired pipeline syntax with a PowerShell inspired CLR object runtime.");
        await Console.Out.WriteLineAsync("Everything is an Object.");
        await Console.Out.WriteLineAsync(string.Empty);
    }
}

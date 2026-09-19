using Tosh.Runtime;
using Tosh.Language;
using Tosh.Cli.Tui;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tosh.Tui.Editing;

namespace Tosh.Cli;

public sealed class ToshRepl
{
    private readonly DiagnosticRenderer _diagnostics;
    private readonly ReplCompletionEngine _completionEngine;
    private readonly ReplCommandLineInsertionSink _commandLineInsertion;
    private readonly ToshEngine _engine;
    private readonly ReplLineEditor _lineEditor;
    private readonly ToshRuntime _runtime;
    private CancellationTokenSource? _activeExecutionCancellation;

    public ToshRepl(ToshEngine engine)
    {
        _engine = engine;
        _engine.IsInteractiveSession = true;
        _runtime = engine.LanguageRuntime.CommandHost as ToshRuntime
            ?? throw new InvalidOperationException("The TōSh REPL requires a TōSh command host.");
        _diagnostics = new DiagnosticRenderer(_runtime.Config.Theme.Diagnostics, _runtime.Config.Diagnostics);
        _lineEditor = new ReplLineEditor();
        _commandLineInsertion = new ReplCommandLineInsertionSink();
        _runtime.CommandLineInsertion = _commandLineInsertion;
        _completionEngine = new ReplCompletionEngine(_runtime);

        // Let display profiles (e.g. HelpTopic example block) reuse the REPL syntax highlighter.
        if (_runtime.Display is not null)
        {
            _runtime.Display.CodeHighlighter = text => SyntaxHighlighter.Highlight(text, _runtime);
        }
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
                    await BuildPromptAsync(),
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
                var startedAt = DateTimeOffset.Now;
                _runtime.SetLastStartedAt(startedAt);
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    if (await ExecuteAndPrintInterruptiblyAsync(source, sourceName))
                    {
                        // Successful command — clear any prior diagnostic so $tosh.Last.HasError reflects reality.
                        _runtime.SetLastDiagnostic(null, null);
                    }
                    else
                    {
                        _runtime.SetLastExitCode(130);
                        _runtime.SetLastDiagnostic(null, null);
                        await Console.Out.WriteLineAsync("^C");
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    _runtime.SetLastCommandDuration(stopwatch.Elapsed);
                }
            }
            catch (Exception exception)
            {
                var rendered = _diagnostics.Render(exception);
                _runtime.SetLastDiagnostic(rendered, exception);
                await Console.Error.WriteLineAsync(rendered);
            }

            if (_runtime.ExitRequested)
            {
                break;
            }
        }
    }

    internal async Task<bool> ExecuteAndPrintInterruptiblyAsync(string source, string sourceName)
    {
        using var cancellation = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _activeExecutionCancellation, cancellation, null) is not null)
        {
            throw new InvalidOperationException("A REPL command is already executing.");
        }

        IDisposable? interruptRegistration = null;

        try
        {
            interruptRegistration = RegisterExecutionInterrupt();
            await ExecuteAndPrintAsync(source, sourceName, cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeExecutionCancellation, null, cancellation);
            interruptRegistration?.Dispose();
        }
    }

    internal bool TryInterruptCurrentExecution()
    {
        var cancellation = Volatile.Read(ref _activeExecutionCancellation);
        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException)
        {
            // Cancellation callbacks belong to the running command. An
            // interrupt must still return control to the REPL even if one
            // callback is faulty.
            return true;
        }
    }

    private IDisposable RegisterExecutionInterrupt()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                TryInterruptCurrentExecution();
            });
        }

        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            TryInterruptCurrentExecution();
        };
        Console.CancelKeyPress += handler;
        return new CallbackDisposable(() => Console.CancelKeyPress -= handler);
    }

    private async Task ExecuteAndPrintAsync(
        string source,
        string sourceName,
        CancellationToken cancellationToken)
    {
        await using var sink = new AutoDisplaySink(_runtime, renderTuiOutcome: true);
        await foreach (var value in _engine.EvaluateAsync(source, sourceName, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            await sink.EmitAsync(value, cancellationToken);
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose()
        {
            Interlocked.Exchange(ref _callback, null)?.Invoke();
        }
    }

    private async Task<string> BuildPromptAsync()
    {
        if (_runtime.Commands.TryGet("prompt", out _))
        {
            try
            {
                var results = await _engine.ExecuteToListAsync("prompt", "<prompt>");

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

    /// <summary>
    /// Prints the greeting, if the reader wants one and it has anything to say.
    /// </summary>
    /// <remarks>
    /// The text is a template rather than a constant, so <c>{$env.USER}</c> reads as it
    /// would anywhere else. A banner that fails to evaluate prints as written instead of
    /// stopping the shell: this runs before the first prompt of somebody's login shell, and
    /// a greeting is never worth refusing to start over.
    /// </remarks>
    private async Task PrintBannerAsync()
    {
        var startup = _runtime.Config.Startup;

        if (!startup.DisplayBanner || string.IsNullOrWhiteSpace(startup.BannerContents))
        {
            return;
        }

        await Console.Out.WriteLineAsync(await RenderBannerAsync(startup.BannerContents));
        await Console.Out.WriteLineAsync(string.Empty);
    }

    /// <summary>Evaluates the banner's interpolation holes.</summary>
    /// <remarks>
    /// <para>
    /// The template is wrapped in a triple-quoted interpolated string and evaluated, which
    /// is what gives it the same holes every other string has. The fence is longer than the
    /// longest run of quotes in the text, so a banner containing <c>"""</c> cannot close
    /// it early — the rule C# raw strings use, for the same reason.
    /// </para>
    /// <para>
    /// Written flush against the fence so the trimming rule leaves the text alone: a banner
    /// is printed as it was typed.
    /// </para>
    /// </remarks>
    private async Task<string> RenderBannerAsync(string template)
    {
        try
        {
            var fence = new string('"', Math.Max(3, LongestQuoteRun(template) + 1));
            var rendered = new List<object?>();

            await foreach (var value in _engine.EvaluateAsync($"${fence}\n{template}\n{fence}"))
            {
                rendered.Add(value);
            }

            return rendered.Count == 1 && rendered[0] is string text ? text : template;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Said once, quietly, on the stream nobody pipes: the shell still opens.
            await Console.Error.WriteLineAsync(
                $"tosh: the startup banner could not be evaluated ({exception.Message}); printing it as written.");

            return template;
        }
    }

    private static int LongestQuoteRun(string text)
    {
        var longest = 0;
        var run = 0;

        foreach (var character in text)
        {
            run = character == '"' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }
}

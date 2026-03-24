using Tosh.Core;
using Tosh.Language;

namespace Tosh.Cli;

public sealed class ToshRepl
{
    private readonly DiagnosticRenderer _diagnostics;
    private readonly ToshEngine _engine;
    private readonly ReplLineEditor _lineEditor;
    private readonly ToshRuntime _runtime;

    public ToshRepl(ToshEngine engine)
    {
        _engine = engine;
        _diagnostics = new DiagnosticRenderer();
        _lineEditor = new ReplLineEditor();
        _runtime = engine.Runtime;
    }

    public async Task RunAsync()
    {
        await PrintBannerAsync();

        var buffer = new List<string>();
        string[]? cachedHistory = null;
        var lastHistoryCount = -1;

        while (true)
        {
            if (lastHistoryCount != _runtime.History.Count)
            {
                cachedHistory = _runtime.History.Select(entry => entry.Text).ToArray();
                lastHistoryCount = _runtime.History.Count;
            }

            var prompt = buffer.Count == 0 ? BuildPrompt() : "....> ";
            var line = _lineEditor.ReadLine(prompt, cachedHistory!);

            if (line is null)
            {
                break;
            }

            var trimmed = line.Trim();

            if (buffer.Count == 0 && trimmed.Length == 0)
            {
                continue;
            }

            buffer.Add(line);

            if (ReplInputClassifier.RequiresContinuation(buffer))
            {
                continue;
            }

            var source = string.Join(Environment.NewLine, buffer);
            buffer.Clear();
            _runtime.RecordHistory(source);

            try
            {
                var sourceName = $"repl_entry #{_runtime.History[^1].Index}";
                await ExecuteAndPrintAsync(source, sourceName);
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
        var rendered = _runtime.Display.RenderMany(values, ConsoleDisplay.CreateRenderOptions(_runtime));

        if (rendered.Length > 0)
        {
            await Console.Out.WriteLineAsync(rendered);
        }
    }

    private string BuildPrompt()
    {
        var currentDirectory = _runtime.CurrentDirectory;
        var home = PathUtilities.UserHomeDirectory;

        if (currentDirectory.StartsWith(home, PathUtilities.GetPathComparison()))
        {
            currentDirectory = $"~{currentDirectory[home.Length..]}";
        }

        return $"tosh {currentDirectory}> ";
    }

    private static async Task PrintBannerAsync()
    {
        await Console.Out.WriteLineAsync("tosh (ToastedSHell)");
        await Console.Out.WriteLineAsync("Nu-inspired pipeline syntax with a CLR object runtime.");
        await Console.Out.WriteLineAsync("Everything in the session is a shell command.");
        await Console.Out.WriteLineAsync("Prompt editing supports arrows, home/end, delete/backspace, and history recall.");
        await Console.Out.WriteLineAsync("Try: help search json, man where, alias ll = ls -la, def recent(days) { ls -la | where Modified > ((date now) - $days) }, source ~/.config/tosh/profile.tosh, exit");
        await Console.Out.WriteLineAsync(string.Empty);
    }
}

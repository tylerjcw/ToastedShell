using Tosh.Core;
using Tosh.Language.Debugging;

namespace Tosh.Language.Commands.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("path", "Path to the Tosh script to debug.", Required = true, TypeName = "string")]
[CommandExample("debug ./script.tosh", Title = "Debug a script with step-through execution.")]
[CommandExample("debug ./script.tosh arg1 arg2", Title = "Debug a script passing arguments.")]
[CommandNote("Use 'n' or 'next' to step, 'c' or 'continue' to resume, 'q' or 'quit' to abort, 'vars' to inspect variables.")]
[CommandOutput("Streams whatever the wrapped pipeline produces; emits diagnostic events as a side effect.")]
public sealed class DebugCommand : ShellCommand
{
    private readonly ToshEngine _engine;

    public DebugCommand(ToshEngine engine)
        : base("debug", "Runs a Tosh script with interactive step-through debugging.", "debug <path> [args...]")
    {
        _engine = engine;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("The 'debug' command requires a script path.");
        }

        var rawPath = context.Arguments[0]?.ToString();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidOperationException("The 'debug' command requires a non-empty script path.");
        }

        var path = Path.GetFullPath(rawPath, context.Runtime.CurrentDirectory);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Script not found: '{rawPath}'.", path);
        }

        var scriptArgs = context.Arguments.Skip(1).ToArray();
        var stepping = true;

        var previousHook = _engine.DebugHook;
        _engine.DebugHook = async stepContext =>
        {
            if (!stepping)
            {
                return DebugAction.Continue;
            }

            var lineDisplay = stepContext.Line.HasValue ? $"{stepContext.Line}" : "?";
            var text = stepContext.StatementText ?? "<unknown>";

            // Truncate long statements for display.
            if (text.Length > 120)
            {
                text = string.Concat(text.AsSpan(0, 117), "...");
            }

            await context.Runtime.Error.WriteLineAsync($"[debug] {stepContext.SourceName}:{lineDisplay}  {text}");
            await context.Runtime.Error.WriteAsync("(debug) ");

            while (true)
            {
                var input = await ReadDebugInputAsync();
                if (input is null)
                {
                    return DebugAction.Abort;
                }

                var trimmed = input.Trim();

                switch (trimmed)
                {
                    case "n" or "next" or "s" or "step" or "":
                        return DebugAction.StepNext;
                    case "c" or "continue":
                        stepping = false;
                        return DebugAction.Continue;
                    case "q" or "quit" or "abort":
                        return DebugAction.Abort;
                    case "vars" or "variables" or "locals":
                        PrintLocals(_engine, context.Runtime);
                        await context.Runtime.Error.WriteAsync("(debug) ");
                        continue;
                    default:
                        await context.Runtime.Error.WriteLineAsync("  n/next   - step to next statement");
                        await context.Runtime.Error.WriteLineAsync("  c/cont   - continue execution");
                        await context.Runtime.Error.WriteLineAsync("  q/quit   - abort execution");
                        await context.Runtime.Error.WriteLineAsync("  vars     - show local variables");
                        await context.Runtime.Error.WriteAsync("(debug) ");
                        continue;
                }
            }
        };

        List<object?> results;
        try
        {
            results = await AsyncEnumerableExtensions.ToListAsync(
                _engine.ExecuteScriptFileAsync(path, scriptArgs, cancellationToken: context.CancellationToken),
                context.CancellationToken);
        }
        catch (DebugAbortException)
        {
            await context.Runtime.Error.WriteLineAsync("[debug] Execution aborted.");
            results = [];
        }
        finally
        {
            _engine.DebugHook = previousHook;
        }

        foreach (var value in results)
        {
            yield return value;
        }
    }

    private static async Task<string?> ReadDebugInputAsync()
    {
        // Read from stdin. If redirected or EOF, return null.
        if (Console.IsInputRedirected)
        {
            return await Task.FromResult(Console.ReadLine());
        }

        return Console.ReadLine();
    }

    private static void PrintLocals(ToshEngine engine, ToshRuntime runtime)
    {
        var variables = engine.GetVisibleVariables();

        if (variables.Count == 0)
        {
            runtime.Error.WriteLine("  (no local variables)");
        }
        else
        {
            foreach (var kvp in variables)
            {
                runtime.Error.WriteLine($"  ${kvp.Key} = {FormatValue(kvp.Value)}");
            }
        }

        var lastResult = runtime.LastResult;
        if (lastResult is not null)
        {
            runtime.Error.WriteLine($"  $_ = {FormatValue(lastResult)}");
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            _ => value.ToString() ?? "null",
        };
    }
}

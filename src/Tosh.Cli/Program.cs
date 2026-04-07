using Tosh.Cli;
using Tosh.Cli.Tui;
using Tosh.Core;
using Tosh.Language;

var runtime = ToshRuntime.CreateDefault(Console.Out, Console.Error);
runtime.InlinePrompts = new ConsoleInlinePromptProvider(runtime);
var engine = new ToshEngine(runtime);
var diagnostics = new DiagnosticRenderer(runtime.Config.Theme.Diagnostics);
CliInvocationPlan plan;

try
{
    plan = CliInvocationResolver.Resolve(args, runtime.CurrentDirectory);
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(diagnostics.Render(exception));
    Environment.ExitCode = 1;
    return;
}

if (plan.Kind == CliInvocationKind.Help)
{
    await PrintUsageAsync();
    return;
}

if (plan.Kind == CliInvocationKind.ExportManifest)
{
    await ExportCommandManifestAsync(plan);
    return;
}

if (plan.LoadStartup)
{
    try
    {
        await ToshStartupLoader.LoadAsync(engine, configDirectory: null, skipProfile: plan.SkipProfile, errorWriter: Console.Error);
    }
    catch (Exception exception)
    {
        // Config file errors are still fatal — the shell can't function without config.
        await Console.Error.WriteLineAsync(diagnostics.Render(exception));
        Environment.ExitCode = 1;
        return;
    }
}

runtime.IsLoginShell = plan.IsLoginShell;

try
{
    runtime.InitializeHistoryStorage(writeThrough: plan.Kind == CliInvocationKind.Repl);
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(diagnostics.Render(exception));
}

try
{
    runtime.InitializeDirectoryStackStorage();
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(diagnostics.Render(exception));
}

await RaiseSessionStartedAsync();

if (plan.Kind != CliInvocationKind.Repl)
{
    try
    {
        switch (plan.Kind)
        {
            case CliInvocationKind.Command:
                runtime.InvocationArguments = plan.Arguments.Cast<object?>().ToArray();
                await ExecuteAndPrintAsync(plan.ScriptOrCommand!);
                Environment.ExitCode = runtime.LastExitCode;
                break;
            case CliInvocationKind.ToshScript:
                runtime.InvocationArguments = plan.Arguments.Cast<object?>().ToArray();
                await ExecuteFileAndPrintAsync(plan.ScriptOrCommand!, plan.Arguments);
                Environment.ExitCode = runtime.LastExitCode;
                break;
            case CliInvocationKind.ExternalScript:
                await ExecuteAndPrintAsync(string.Join(" ", plan.Arguments.Select(QuoteArgument)));
                Environment.ExitCode = runtime.LastExitCode;
                break;
            default:
                throw new InvalidOperationException($"Unsupported CLI invocation kind '{plan.Kind}'.");
        }
    }
    catch (Exception exception)
    {
        await Console.Error.WriteLineAsync(diagnostics.Render(exception));
        Environment.ExitCode = 1;
    }

    await RaiseSessionEndingAsync();
    return;
}

var repl = new ToshRepl(engine);
await repl.RunAsync();
runtime.KillAllJobs();
await RaiseSessionEndingAsync();
Environment.ExitCode = runtime.LastExitCode;
return;

async Task ExecuteAndPrintAsync(string source)
{
    var historyEntry = runtime.RecordHistory(source);
    var sourceName = historyEntry is not null
        ? $"commandline #{historyEntry.Id}"
        : $"commandline transient";
    var values = await engine.ExecuteToListAsync(source, sourceName);

    try
    {
        if (TuiRequestDispatcher.TryHandle(values, runtime))
        {
            return;
        }

        var rendered = runtime.Display.RenderMany(values, ConsoleDisplay.CreateRenderOptions(runtime));

        await ConsoleDisplay.WriteRenderedAsync(rendered, runtime);
    }
    finally
    {
        runtime.ClearDisplaySelections();
    }
}

async Task ExecuteFileAndPrintAsync(string path, IReadOnlyList<object?> arguments)
{
    runtime.RecordHistory(path);
    var values = await AsyncEnumerableExtensions.ToListAsync(engine.ExecuteScriptFileAsync(path, arguments), default);

    try
    {
        if (TuiRequestDispatcher.TryHandle(values, runtime))
        {
            return;
        }

        var rendered = runtime.Display.RenderMany(values, ConsoleDisplay.CreateRenderOptions(runtime));

        await ConsoleDisplay.WriteRenderedAsync(rendered, runtime);
    }
    finally
    {
        runtime.ClearDisplaySelections();
    }
}

async Task RaiseSessionStartedAsync()
{
    try
    {
        var sender = runtime.EventSenderFactory?.Invoke()
            ?? new ShellEventSender(Function: null, Script: null, Line: null);
        var evt = new SessionStartedEvent(DateTimeOffset.Now, runtime.Config.Startup.RootDirectory, sender);
        await runtime.Events.RaiseAsync(evt, CancellationToken.None);
    }
    catch
    {
        // Don't let event handler failures prevent startup.
    }
}

async Task RaiseSessionEndingAsync()
{
    try
    {
        var sender = runtime.EventSenderFactory?.Invoke()
            ?? new ShellEventSender(Function: null, Script: null, Line: null);
        var evt = new SessionEndingEvent(runtime.LastExitCode, sender);
        await runtime.Events.RaiseAsync(evt, CancellationToken.None);
    }
    catch
    {
        // Don't let event handler failures prevent shutdown.
    }
}

static string QuoteArgument(string argument)
{
    if (argument.Length == 0 || argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '|' or '#'))
    {
        return $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    return argument;
}

static async Task PrintUsageAsync()
{
    await Console.Out.WriteLineAsync("Usage:");
    await Console.Out.WriteLineAsync("  tosh");
    await Console.Out.WriteLineAsync("  tosh -c 'echo hello | type-of'");
    await Console.Out.WriteLineAsync("  tosh --no-startup");
    await Console.Out.WriteLineAsync("  tosh script.tosh [args...]");
    await Console.Out.WriteLineAsync("  tosh ./script-with-shebang [args...]");
    await Console.Out.WriteLineAsync("  tosh -- <command-or-script-starting-with-dash> [args...]");
    await Console.Out.WriteLineAsync(string.Empty);
    await Console.Out.WriteLineAsync("Flags:");
    await Console.Out.WriteLineAsync("  -h, --help       Show usage");
    await Console.Out.WriteLineAsync("  -c, --command    Run one ToSh command string and exit");
    await Console.Out.WriteLineAsync("  -l, --login      Start as a login shell");
    await Console.Out.WriteLineAsync("  --no-startup     Skip config.tosh, profile.tosh, and autoload startup files");
    await Console.Out.WriteLineAsync("  --no-profile     Skip profile.tosh (config.tosh and autoload still load)");
    await Console.Out.WriteLineAsync("  --               Stop flag parsing for the next argument");
    await Console.Out.WriteLineAsync(string.Empty);
    await Console.Out.WriteLineAsync("Examples:");
    await Console.Out.WriteLineAsync("  tosh 'help'");
    await Console.Out.WriteLineAsync("  tosh -c 'help search json'");
    await Console.Out.WriteLineAsync("  tosh 'help search json'");
    await Console.Out.WriteLineAsync("  tosh 'config'");
    await Console.Out.WriteLineAsync("  tosh 'config reload'");
    await Console.Out.WriteLineAsync("  tosh 'config set prompt.name-text toast'");
    await Console.Out.WriteLineAsync("  tosh ./examples/library_demo.tosh");
    await Console.Out.WriteLineAsync("  tosh ./script-with-foreign-shebang");
    await Console.Out.WriteLineAsync("  tosh 'help where'");
    await Console.Out.WriteLineAsync("  tosh 'view detail'");
    await Console.Out.WriteLineAsync("  tosh 'ls -la'");
    await Console.Out.WriteLineAsync("  tosh 'echo \"Hello\".ToLower()'");
    await Console.Out.WriteLineAsync("  tosh 'echo String.Join(\" \", [\"Hello\", \"World\"])'");
    await Console.Out.WriteLineAsync("  tosh 'writeline \"hello\"'");
    await Console.Out.WriteLineAsync("  tosh 'mkdir -p scratch | get FullName'");
    await Console.Out.WriteLineAsync("  tosh 'func ll => ls -la'");
    await Console.Out.WriteLineAsync("  tosh 'require ./common.tosh'");
    await Console.Out.WriteLineAsync("  tosh 'func llf => ls -la | where _.Type == file'");
    await Console.Out.WriteLineAsync("  tosh 'func recent(days: TimeSpan) { ls -la | where _.Modified > ((date now) - $days) }'");
}

static async Task ExportCommandManifestAsync(CliInvocationPlan plan)
{
    var format = plan.ScriptOrCommand ?? "json";
    var outputPath = plan.Arguments.Length > 0 ? plan.Arguments[0] : null;

    // Build a minimal runtime just for command registration — no startup/config needed.
    var registry = new ShellCommandRegistry();
    Tosh.Core.Commands.BuiltInCommands.RegisterDefaults(registry);

    string output;

    if (string.Equals(format, "latex", StringComparison.OrdinalIgnoreCase))
    {
        var manifest = CommandManifestExporter.BuildManifest(registry);
        output = CommandLatexEmitter.Emit(manifest);
    }
    else
    {
        output = CommandManifestExporter.ExportJson(registry);
    }

    if (outputPath is not null)
    {
        var dir = Path.GetDirectoryName(outputPath);

        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputPath, output);
        await Console.Error.WriteLineAsync($"Wrote {format} manifest to {outputPath}");
    }
    else
    {
        await Console.Out.WriteAsync(output);
    }
}

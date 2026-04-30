using System.Runtime.InteropServices;
using System.Text;
using Tosh.Cli;
using Tosh.Cli.Tui;
using Tosh.Core;
using Tosh.Language;

ConfigureConsoleEncoding();

var runtime = ToshRuntime.CreateDefault(Console.Out, Console.Error);
runtime.InlinePrompts = new ConsoleInlinePromptProvider(runtime);
var engine = new ToshEngine(runtime);

// Strip diagnostic-output overrides before resolving the invocation plan so
// that user-facing flags (`--diagnostics=json|text|plain`) take effect for any
// errors raised during arg parsing itself.
args = ApplyDiagnosticFlags(args, runtime.Config.Diagnostics);

var diagnostics = new DiagnosticRenderer(runtime.Config.Theme.Diagnostics, runtime.Config.Diagnostics);
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

if (plan.Kind == CliInvocationKind.Version)
{
    await PrintVersionAsync();
    return;
}

if (plan.Kind == CliInvocationKind.ExportMetadata)
{
    await ExportCommandMetadataAsync(plan);
    return;
}

// Set login shell flag before startup so $tosh.IsLoginShell is visible in config/profile scripts.
runtime.IsLoginShell = plan.IsLoginShell;

if (plan.IsLoginShell)
{
    InitializeLoginShellEnvironment();
}

if (plan.LoadStartup)
{
    await ToshStartupLoader.LoadAsync(engine, configDirectory: null, skipProfile: plan.SkipProfile, errorWriter: Console.Error, profileStartup: plan.ProfileStartup);
}

if (plan.SafeMode)
{
    await Console.Error.WriteLineAsync("tosh: safe mode — config, profile, and autoload files were skipped.");
}

var historyStopwatch = plan.ProfileStartup ? System.Diagnostics.Stopwatch.StartNew() : null;
try
{
    runtime.InitializeHistoryStorage(writeThrough: plan.Kind == CliInvocationKind.Repl);
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(diagnostics.Render(exception));
}
finally
{
    if (historyStopwatch is not null)
    {
        historyStopwatch.Stop();
        if (runtime.StartupProfile is { } profile)
        {
            profile.History = historyStopwatch.Elapsed;
        }
    }
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

if (plan.ProfileStartup && runtime.StartupProfile is { } startupProfile)
{
    PrintStartupProfile(startupProfile);
}

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

PosixSignalRegistration? sighupRegistration = null;
PosixSignalRegistration? sigtermRegistration = null;

// Ensure terminal state is restored even on abnormal exit.
AppDomain.CurrentDomain.ProcessExit += (_, _) => runtime.Terminal.RestoreTerminalState();

if (!OperatingSystem.IsWindows())
{
    sighupRegistration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ =>
    {
        runtime.Terminal.RestoreTerminalState();
        runtime.KillAllJobs();
        Environment.Exit(128 + 1); // SIGHUP
    });

    sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
    {
        runtime.Terminal.RestoreTerminalState();
        runtime.KillAllJobs();
        Environment.Exit(128 + 15); // SIGTERM
    });
}

try
{
    var repl = new ToshRepl(engine);
    await repl.RunAsync();
}
finally
{
    sighupRegistration?.Dispose();
    sigtermRegistration?.Dispose();
    runtime.Terminal.RestoreTerminalState();
}

if (plan.IsLoginShell)
{
    await RunLogoutHookAsync();
}

runtime.KillAllJobs();
await RaiseSessionEndingAsync();
Environment.ExitCode = runtime.LastExitCode;
return;

static void ConfigureConsoleEncoding()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    try
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = utf8;
        Console.OutputEncoding = utf8;
    }
    catch
    {
        // Keep startup resilient if the host rejects encoding changes.
    }
}

static string[] ApplyDiagnosticFlags(string[] arguments, ToshDiagnosticsConfig diagnostics)
{
    var remaining = new List<string>(arguments.Length);

    foreach (var arg in arguments)
    {
        if (arg.StartsWith("--diagnostics=", StringComparison.Ordinal))
        {
            var value = arg["--diagnostics=".Length..];
            switch (value)
            {
                case "json":
                    diagnostics.Format = ToshDiagnosticFormat.Json;
                    break;
                case "text":
                    diagnostics.Format = ToshDiagnosticFormat.Text;
                    diagnostics.PlainOutput = false;
                    break;
                case "plain":
                    diagnostics.Format = ToshDiagnosticFormat.Text;
                    diagnostics.PlainOutput = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown --diagnostics value '{value}'. Expected 'text', 'plain', or 'json'.");
            }
            continue;
        }

        remaining.Add(arg);
    }

    return remaining.ToArray();
}

void InitializeLoginShellEnvironment()
{
    // Set SHELL to the current executable so child processes inherit it.
    var exePath = Environment.ProcessPath;
    if (!string.IsNullOrEmpty(exePath))
    {
        Environment.SetEnvironmentVariable("SHELL", exePath);
    }

    // Ensure the directory containing tosh is on PATH.
    if (!string.IsNullOrEmpty(exePath))
    {
        var exeDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var dirs = currentPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (!dirs.Contains(exeDir, StringComparer.Ordinal))
            {
                Environment.SetEnvironmentVariable("PATH", $"{exeDir}:{currentPath}");
            }
        }
    }

    // Ensure standard identity env vars are set (PAM/systemd may or may not provide these).
    SetIfMissing("USER", Environment.UserName);
    SetIfMissing("LOGNAME", Environment.UserName);
    SetIfMissing("HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    static void SetIfMissing(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) && !string.IsNullOrEmpty(value))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

async Task RunLogoutHookAsync()
{
    var root = runtime.Config.Startup.RootDirectory;
    var logoutPath = Path.Combine(root, "logout.tosh");

    if (!File.Exists(logoutPath))
    {
        return;
    }

    try
    {
        var source = await File.ReadAllTextAsync(logoutPath);
        await AsyncEnumerableExtensions.ToListAsync(engine.EvaluateAsync(source, logoutPath), default);
    }
    catch (Exception exception)
    {
        await Console.Error.WriteLineAsync($"tosh: error in logout hook: {exception.Message}");
    }
}

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

static void PrintStartupProfile(Tosh.Core.StartupProfileData profile)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Startup Profile");
    Console.Error.WriteLine("───────────────────────────────────");
    Console.Error.WriteLine($"  Total:    {profile.Total.TotalMilliseconds,8:F1} ms");
    Console.Error.WriteLine($"  Config:   {profile.Config.TotalMilliseconds,8:F1} ms");
    Console.Error.WriteLine($"  Profile:  {profile.Profile.TotalMilliseconds,8:F1} ms");
    Console.Error.WriteLine($"  Autoload: {profile.Autoload.TotalMilliseconds,8:F1} ms");
    Console.Error.WriteLine($"  History:  {profile.History.TotalMilliseconds,8:F1} ms");

    if (profile.Files.Count > 0)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Files:");
        foreach (var file in profile.Files)
        {
            Console.Error.WriteLine($"    {file.Duration.TotalMilliseconds,8:F1} ms  {file.Path}");
        }
    }

    Console.Error.WriteLine();
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
    await Console.Out.WriteLineAsync("  -h, --help            Show usage");
    await Console.Out.WriteLineAsync("  -V, --version         Print version and exit");
    await Console.Out.WriteLineAsync("  -c, --command         Run one ToSh command string and exit");
    await Console.Out.WriteLineAsync("  -l, --login           Start as a login shell");
    await Console.Out.WriteLineAsync("  --no-startup          Skip config.tosh, profile.tosh, and autoload startup files");
    await Console.Out.WriteLineAsync("  --no-profile          Skip profile.tosh (config.tosh and autoload still load)");
    await Console.Out.WriteLineAsync("  --safe                Start in safe mode (skip all startup, guaranteed recovery)");
    await Console.Out.WriteLineAsync("  --profile-startup     Show startup phase timing breakdown");
    await Console.Out.WriteLineAsync("  --diagnostics=MODE    Override diagnostic output mode (text|plain|json)");
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

static async Task PrintVersionAsync()
{
    var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
        Attribute.GetCustomAttribute(
            typeof(CliInvocationResolver).Assembly,
            typeof(System.Reflection.AssemblyInformationalVersionAttribute));
    var version = attr?.InformationalVersion ?? "unknown";
    await Console.Out.WriteLineAsync($"tosh {version}");
}

static async Task ExportCommandMetadataAsync(CliInvocationPlan plan)
{
    var format = plan.ScriptOrCommand ?? "json";
    var outputPath = plan.Arguments.Length > 0 ? plan.Arguments[0] : null;

    // Build a minimal runtime just for command registration — no startup/config needed.
    // Use a real ToshRuntime + ToshEngine so engine-supplied built-ins (source, debug) are included.
    var runtime = ToshRuntime.CreateDefault();
    _ = new ToshEngine(runtime);
    var registry = runtime.Commands;

    string output;

    if (string.Equals(format, "latex", StringComparison.OrdinalIgnoreCase))
    {
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        output = CommandLatexEmitter.Emit(metadata);
    }
    else if (string.Equals(format, "vscode", StringComparison.OrdinalIgnoreCase))
    {
        var metadata = CommandMetadataExporter.BuildMetadata(registry);
        output = VsCodeMetadataEmitter.Emit(metadata);
    }
    else
    {
        output = CommandMetadataExporter.ExportMetadataJson(registry);
    }

    if (outputPath is not null)
    {
        var dir = Path.GetDirectoryName(outputPath);

        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputPath, output);
        await Console.Error.WriteLineAsync($"Wrote {format} metadata to {outputPath}");
    }
    else
    {
        await Console.Out.WriteAsync(output);
    }
}

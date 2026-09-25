using System.Runtime.InteropServices;
using System.Text;
using Tosh.Cli;
using Tosh.Cli.Tui;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Language.Binding;

ConfigureConsoleEncoding();

var runtime = ToshRuntime.CreateDefault(Console.Out, Console.Error);
runtime.InlinePrompts = new ConsoleInlinePromptProvider(runtime);
runtime.TuiScreens = new ConsoleTuiScreenRunner(runtime);
Tosh.Cli.Tui.ShellWidgetDefaults.Install(runtime);

// A TōSh that was killed outright — SIGKILL, or a machine that lost power — never got to
// hand the terminal back, and the reader is sitting in the alternate screen with no cursor.
// Only when a note from that session is still here for this terminal: a shell should not
// write escapes at every start on the chance that something is broken (TUI-0010).
Tosh.Tui.TuiTerminalRepair.RepairIfNeeded(Console.Out.Write);
var engine = new ToshEngine(runtime.Language);

// Strip diagnostic-output overrides before resolving the invocation plan so
// that user-facing flags (`--diagnostics=json|text|plain`) take effect for any
// errors raised during arg parsing itself.
args = ApplyDiagnosticFlags(args, runtime.Config.Diagnostics);

// `--serve <socket>` — Phase 0 experiment. Holds a warm engine behind a unix
// socket so a client pays a round-trip instead of CLR startup plus the ~180 ms
// platform type index. Stripped before plan resolution so the remaining args
// resolve to a normal REPL plan, which is what loads config/profile/autoload.
string? servePath = null;
{
    var serveIndex = Array.IndexOf(args, "--serve");

    if (serveIndex >= 0 && serveIndex + 1 < args.Length)
    {
        servePath = args[serveIndex + 1];
        args = args.Where((_, i) => i != serveIndex && i != serveIndex + 1).ToArray();
    }
}

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

// `TS-P2-110`. `--help`, `--version` and `--export-metadata` return before any
// startup file is read, so the flag has nothing to measure. Saying so beats
// exiting 0 with no output, which reads as a measurement of zero.
if (plan.ProfileStartup && plan.Kind is CliInvocationKind.Help
        or CliInvocationKind.Version or CliInvocationKind.ExportMetadata)
{
    await Console.Error.WriteLineAsync(
        "tosh: --profile-startup has nothing to report for this invocation — " +
        "it measures the startup files, and this one does not load them.");
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

// Set interactive flag before loading startup files so the engine suppresses
// type-checker warnings that fire on function bodies in autoload/profile scripts.
if (plan.Kind == CliInvocationKind.Repl)
    engine.IsInteractiveSession = true;

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
else if (plan.ProfileStartup)
{
    // `TS-P2-110`. The flag used to reach only the REPL plan, so every other
    // invocation accepted it, printed nothing and exited 0 — which reads as
    // "startup is free" rather than "nothing was measured". Where there is
    // genuinely no startup to profile, say so instead of staying quiet.
    await Console.Error.WriteLineAsync(
        "tosh: --profile-startup has nothing to report for this invocation — " +
        "it measures the startup files, and this one does not load them.");
}

if (servePath is not null)
{
    await RunSocketServerAsync(servePath);
    await RaiseSessionEndingAsync();
    return;
}

if (plan.Kind != CliInvocationKind.Repl)
{
    try
    {
        switch (plan.Kind)
        {
            case CliInvocationKind.Command:
                runtime.InvocationArguments = plan.Arguments.Cast<object?>().ToArray();
                using (engine.PushBinderStrictness(BinderStrictness.Strict))
                {
                    await ExecuteAndPrintAsync(plan.ScriptOrCommand!);
                }
                Environment.ExitCode = runtime.LastExitCode;
                break;
            case CliInvocationKind.ToshScript:
                runtime.InvocationArguments = plan.Arguments.Cast<object?>().ToArray();
                await ExecuteFileAndPrintAsync(plan.ScriptOrCommand!, plan.Arguments);
                Environment.ExitCode = runtime.LastExitCode;
                break;
            case CliInvocationKind.ExternalScript:
                using (engine.PushBinderStrictness(BinderStrictness.Strict))
                {
                    await ExecuteAndPrintAsync(string.Join(" ", plan.Arguments.Select(QuoteArgument)));
                }
                Environment.ExitCode = runtime.LastExitCode;
                break;
            default:
                throw new InvalidOperationException($"Unsupported CLI invocation kind '{plan.Kind}'.");
        }
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C. The reader asked for this, so there is nothing to report — an error box
        // saying "the operation was canceled" is the shell explaining what they just did.
        // The exit code is the conventional 128 + SIGINT so a caller can still tell.
        Environment.ExitCode = 130;
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
        using (engine.PushBinderStrictness(BinderStrictness.Strict))
        {
            await AsyncEnumerableExtensions.ToListAsync(engine.EvaluateAsync(source, logoutPath), default);
        }
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
    await using var sink = new BufferingDisplaySink(runtime, renderTuiOutcome: false);
    await foreach (var value in engine.EvaluateAsync(source, sourceName))
    {
        await sink.EmitAsync(value);
    }
}

async Task ExecuteFileAndPrintAsync(string path, IReadOnlyList<object?> arguments)
{
    runtime.RecordHistory(path);

    await using var sink = new BufferingDisplaySink(runtime, renderTuiOutcome: false);
    await foreach (var value in engine.ExecuteScriptFileAsync(path, arguments))
    {
        await sink.EmitAsync(value);
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

static void PrintStartupProfile(Tosh.Runtime.StartupProfileData profile)
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

// `tosh --help` is drawn by the same renderer `help <name>` uses, from the topic in
// `ToshHelp`. It was fifty hand-aligned writes before: the shell that renders every
// other command's help as a panel printed its own as flat text, which is the one place
// the inconsistency shows most.
static async Task PrintUsageAsync()
{
    await Console.Out.WriteLineAsync(
        Tosh.Runtime.HelpTopicSummaryRenderer.Render(Tosh.Cli.ToshHelp.Topic(ToshVersion())));
}

static string ToshVersion()
{
    var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
        Attribute.GetCustomAttribute(
            typeof(CliInvocationResolver).Assembly,
            typeof(System.Reflection.AssemblyInformationalVersionAttribute));
    return attr?.InformationalVersion ?? "unknown";
}

static async Task PrintVersionAsync()
    => await Console.Out.WriteLineAsync($"tosh {ToshVersion()}");

static async Task ExportCommandMetadataAsync(CliInvocationPlan plan)
{
    var format = plan.ScriptOrCommand ?? "json";
    var outputPath = plan.Arguments.Length > 0 ? plan.Arguments[0] : null;

    // Build a minimal runtime just for command registration — no startup/config needed.
    // Use a real ToshRuntime + ToshEngine so engine-supplied built-ins (source, debug) are included.
    var runtime = ToshRuntime.CreateDefault();
    _ = new ToshEngine(runtime.Language);
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
    else if (string.Equals(format, "surface", StringComparison.OrdinalIgnoreCase))
    {
        // The language-word registry, for consumers that cannot read C#. The VS Code
        // grammar generator held its own copy of these words for exactly that reason,
        // and drifted (`TS-P2-10`).
        output = LanguageSurfaceExporter.ExportJson();
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


/// <summary>
/// Serves commands from a unix socket against the already-warm engine.
///
/// Deliberately minimal: one command per connection, newline-terminated in,
/// output back, close. No pty, no job control, no session isolation — this
/// exists to answer whether a warm engine makes invocation latency disappear,
/// which is the question that decides whether a native rewrite is worth
/// starting at all.
/// </summary>
async Task RunSocketServerAsync(string socketPath)
{
    if (File.Exists(socketPath))
    {
        File.Delete(socketPath);
    }

    using var listener = new System.Net.Sockets.Socket(
        System.Net.Sockets.AddressFamily.Unix,
        System.Net.Sockets.SocketType.Stream,
        System.Net.Sockets.ProtocolType.Unspecified);

    listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
    listener.Listen(128);

    await Console.Error.WriteLineAsync($"tosh: serving on {socketPath}");

    var stdout = Console.Out;

    while (true)
    {
        using var connection = await listener.AcceptAsync();
        using var stream = new System.Net.Sockets.NetworkStream(connection, ownsSocket: false);

        string? command;

        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            command = await reader.ReadLineAsync();
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            continue;
        }

        var captured = new StringWriter();
        Console.SetOut(captured);

        try
        {
            using (engine.PushBinderStrictness(BinderStrictness.Strict))
            {
                await ExecuteAndPrintAsync(command);
            }
        }
        catch (Exception exception)
        {
            captured.Write(diagnostics.Render(exception));
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var payload = Encoding.UTF8.GetBytes(captured.ToString());
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        connection.Shutdown(System.Net.Sockets.SocketShutdown.Both);
    }
}

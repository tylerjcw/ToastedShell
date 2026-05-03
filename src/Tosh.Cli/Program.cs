using System.Runtime.InteropServices;
using System.Text;
using Microsoft.NET.HostModel;
using Tosh.Cli;
using Tosh.Cli.Tui;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Language.Binding;

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

if (plan.Kind == CliInvocationKind.Compile)
{
    Environment.ExitCode = await CompileScriptAsync(plan, runtime);
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

static async Task<int> CompileScriptAsync(CliInvocationPlan plan, ToshRuntime runtime)
{
    // Compile-plan encoding (see CliInvocationResolver.Compile):
    //   ScriptOrCommand = output path (nullable)
    //   Arguments       = one or more input paths
    var inputPaths = plan.Arguments;
    if (inputPaths.Length == 0)
    {
        await Console.Error.WriteLineAsync("toshc: no input files");
        return 1;
    }

    // Resolve the requested compile profile. Default = Permissive.
    var compileProfile = Tosh.Compiler.CompileProfile.Permissive;
    if (!string.IsNullOrEmpty(plan.CompileProfileName))
    {
        switch (plan.CompileProfileName.Trim().ToLowerInvariant())
        {
            case "permissive":
                compileProfile = Tosh.Compiler.CompileProfile.Permissive;
                break;
            case "runtime":
                compileProfile = Tosh.Compiler.CompileProfile.Runtime;
                break;
            case "pure":
                compileProfile = Tosh.Compiler.CompileProfile.Pure;
                break;
            default:
                await Console.Error.WriteLineAsync(
                    $"toshc: unknown --profile '{plan.CompileProfileName}' (expected: permissive, runtime, pure)");
                return 1;
        }
    }

    foreach (var path in inputPaths)
    {
        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync($"toshc: input not found: {path}");
            return 1;
        }
    }

    var primaryInput = inputPaths[0];
    var outputPath = plan.ScriptOrCommand
        ?? Path.ChangeExtension(primaryInput, ".dll");

    // `dotnet <file>` only recognises `.dll` assemblies. If the
    // user passed `-o foo` or `-o foo.exe`, normalise to `.dll` so
    // the resulting artifact is actually launchable. We keep the
    // user's stem for the assembly name, just fix the extension.
    if (!string.Equals(Path.GetExtension(outputPath), ".dll", StringComparison.OrdinalIgnoreCase))
    {
        outputPath = Path.ChangeExtension(outputPath, ".dll");
    }

    var assemblyName = Path.GetFileNameWithoutExtension(outputPath);

    // Concatenate all input files into a single source for parsing.
    // Each file is preceded by a newline so spans never straddle a
    // file boundary; the source name reflects the first file (used
    // for diagnostics) plus a count when there are extras.
    string source;
    string sourceName;
    if (inputPaths.Length == 1)
    {
        source = await File.ReadAllTextAsync(primaryInput);
        sourceName = primaryInput;
    }
    else
    {
        var parts = new List<string>(inputPaths.Length);
        foreach (var path in inputPaths)
        {
            // Header comment so binder/parser diagnostics point to
            // the right file when multiple sources are merged.
            parts.Add($"# --- {path} ---");
            parts.Add(await File.ReadAllTextAsync(path));
        }
        source = string.Join("\n", parts);
        sourceName = $"{primaryInput} (+{inputPaths.Length - 1} more)";
    }

    var compileEngine = new ToshEngine(runtime);
    var parseResult = compileEngine.Parse(source, sourceName);
    if (parseResult.Diagnostics.Count > 0)
    {
        foreach (var diag in parseResult.Diagnostics)
        {
            await Console.Error.WriteLineAsync($"toshc: {diag}");
        }
        return 1;
    }

    // Run the binder pass against the parsed program. Compiled
    // builds always run binder in strict mode: unknown commands,
    // shell-only commands, and scope-analysis findings become
    // hard errors and the compiler refuses to write an artifact.
    var binderDiagnostics = Tosh.Language.Binding.Binder.Bind(
        parseResult,
        runtime.Commands,
        isInteractive: false);
    if (binderDiagnostics.Count > 0)
    {
        var renderer = new Tosh.Runtime.DiagnosticRenderer(
            runtime.Config.Theme.Diagnostics,
            runtime.Config.Diagnostics);
        foreach (var diagnostic in binderDiagnostics)
        {
            await Console.Error.WriteLineAsync(renderer.Render(diagnostic));
        }
        await Console.Error.WriteLineAsync(
            $"toshc: {binderDiagnostics.Count} binder error(s); no output written.");
        return 1;
    }

    var unit = Lowerer.Lower(parseResult, runtime.Commands);

    // Compile-mode annotation audit. Errors here are always fatal:
    // missing param/return annotations, and (unless
    // `--compile-allow-dynamic` is passed) implicit-dynamic
    // `var` declarations.
    var annotationDiagnostics = Tosh.Language.Binding.TypeChecker.CheckCompileAnnotations(
        unit, allowDynamic: plan.CompileAllowDynamic);
    if (annotationDiagnostics.Count > 0)
    {
        var annotationRenderer = new Tosh.Runtime.DiagnosticRenderer(
            runtime.Config.Theme.Diagnostics,
            runtime.Config.Diagnostics);
        foreach (var diagnostic in annotationDiagnostics)
        {
            await Console.Error.WriteLineAsync(annotationRenderer.Render(diagnostic));
        }
        await Console.Error.WriteLineAsync(
            $"toshc: {annotationDiagnostics.Count} annotation error(s); no output written.");
        return 1;
    }

    // Type-check pass. In compile mode every type diagnostic is
    // promoted to an error: the artifact is only written if the
    // program is well-typed. T3 will introduce the
    // `--compile-allow-dynamic` knob to soften the
    // implicit-dynamic case specifically.
    var typeDiagnostics = Tosh.Language.Binding.TypeChecker.Check(unit);
    if (typeDiagnostics.Count > 0)
    {
        var typeRenderer = new Tosh.Runtime.DiagnosticRenderer(
            runtime.Config.Theme.Diagnostics,
            runtime.Config.Diagnostics);
        foreach (var diagnostic in typeDiagnostics)
        {
            var promoted = Tosh.Language.Binding.TypeChecker.PromoteSeverity(
                diagnostic, Tosh.Runtime.ToshDiagnosticSeverity.Error);
            await Console.Error.WriteLineAsync(typeRenderer.Render(promoted));
        }
        await Console.Error.WriteLineAsync(
            $"toshc: {typeDiagnostics.Count} type error(s); no output written.");
        return 1;
    }

    var dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }

    Tosh.Compiler.EmitResult result;
    using (var fs = File.Create(outputPath))
    {
        result = Tosh.Compiler.BoundUnitEmitter.Emit(unit, assemblyName, fs, compileProfile);
    }

    // Fail-fast: if the emitter reported any unsupported shapes,
    // refuse to ship a half-baked artifact. Delete the partially
    // written .dll so a subsequent `dotnet <out>.dll` doesn't
    // silently run stale output. The companion runtimeconfig is
    // not yet written at this point, so nothing else to clean up.
    if (!result.IsClean)
    {
        await Console.Error.WriteLineAsync(
            $"toshc: {result.UnsupportedShapes.Count} unsupported shape(s):");
        foreach (var shape in result.UnsupportedShapes)
        {
            await Console.Error.WriteLineAsync($"  - {shape}");
        }
        try { File.Delete(outputPath); } catch { /* best-effort cleanup */ }
        await Console.Error.WriteLineAsync(
            "toshc: refusing to write incomplete output.");
        return 1;
    }

    // Emit a minimal runtimeconfig.json so `dotnet <out>.dll` runs.
    var runtimeConfigPath = Path.ChangeExtension(outputPath, ".runtimeconfig.json");
    var runtimeMajor = Environment.Version.Major;
    var runtimeConfig = $$"""
        {
          "runtimeOptions": {
            "tfm": "net{{runtimeMajor}}.0",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "{{Environment.Version}}"
            }
          }
        }
        """;
    await File.WriteAllTextAsync(runtimeConfigPath, runtimeConfig);

    // Stage the runtime DLLs the emitted assembly depends on next
    // to the output so `dotnet <out>.dll` runs without the caller
    // pre-populating its directory. Copies are best-effort: a
    // missing source dll just means the user will see a load
    // failure at runtime, same as before the staging existed.
    StageCompilerRuntime(outputPath);

    var outputDir = Path.GetDirectoryName(outputPath) ?? ".";
    if (plan.EmitAppHost)
    {
        await CreateAppHostAsync(outputPath, outputDir);
        if (plan.PublishSingleFile)
        {
            await CreateBundleAsync(outputPath, outputDir);
        }
    }

    await Console.Error.WriteLineAsync($"toshc: wrote {outputPath}");

    if (plan.EmitRefasm)
    {
        // Reference assembly emission: re-run the emitter with the
        // ReferenceAssembly attribute set so the C# / F# compilers
        // accept the artifact as a metadata-only reference. Sits
        // alongside the main .dll as `<output>.ref.dll`. Method
        // bodies remain populated (fat refasm); body stripping is
        // a deferred optimisation.
        var refOutputPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(outputPath)}.ref{Path.GetExtension(outputPath)}");
        using (var rfs = File.Create(refOutputPath))
        {
            var refResult = Tosh.Compiler.BoundUnitEmitter.Emit(
                unit, assemblyName, rfs, compileProfile, referenceAssembly: true);
            if (!refResult.IsClean)
            {
                await Console.Error.WriteLineAsync(
                    $"toshc: refasm emit reported {refResult.UnsupportedShapes.Count} unsupported shape(s).");
            }
        }
        await Console.Error.WriteLineAsync($"toshc: wrote {refOutputPath}");
    }

    return 0;
}

/// <summary>
/// Copies the compiler-runtime DLLs (and their transitive shell
/// dependencies — language, runtime, stdlib, tui, core) next to a
/// freshly emitted compiled-tosh assembly so it can be launched
/// with <c>dotnet &lt;out&gt;.dll</c> directly.
///
/// Source directory is the directory holding the running
/// <c>tosh</c> binary (a self-contained publish or the dev-build
/// <c>bin/Debug/net10.0</c>). Files only get overwritten when the
/// source is strictly newer to keep repeated compiles cheap.
/// </summary>
static void StageCompilerRuntime(string outputPath)
{
    var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (string.IsNullOrEmpty(outDir)) return;
    var sourceDir = AppContext.BaseDirectory;
    if (string.IsNullOrEmpty(sourceDir) ||
        string.Equals(sourceDir.TrimEnd(Path.DirectorySeparatorChar),
            outDir.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    string[] required =
    {
        "Tosh.Compiler.Runtime.dll",
        "Tosh.Language.dll",
        "Tosh.Runtime.dll",
        "Tosh.Stdlib.dll",
        "Tosh.Core.dll",
        "Tosh.Tui.dll",
    };
    foreach (var name in required)
    {
        var src = Path.Combine(sourceDir, name);
        if (!File.Exists(src)) continue;
        var dst = Path.Combine(outDir, name);
        try
        {
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(src))
            {
                continue;
            }
            File.Copy(src, dst, overwrite: true);
        }
        catch
        {
            // Best-effort: leave the user with the load error if
            // the copy fails (sandbox, read-only fs, etc.).
        }
    }
}

/// <summary>
/// Creates an apphost wrapper executable that points to a .dll.
/// On Windows, the wrapper is named <c>&lt;name&gt;.exe</c>.
/// On Unix, it has no extension and is marked executable.
///
/// NOTE: The AppHost stamping API from Microsoft.NET.HostModel
/// (version 5.0.0-preview) is not compatible with this codebase's
/// usage pattern. Deferred to a future release when the API
/// stabilizes. For now, use 'dotnet publish' with apphost-specific
/// targets for executable generation.
/// </summary>
static async Task CreateAppHostAsync(string dllPath, string outputDir)
{
    await Console.Error.WriteLineAsync("tosh: apphost creation deferred (API incompatibility). Use 'dotnet publish' for executable wrappers.");
}

/// <summary>
/// Bundles all runtime DLLs into a single-file executable using
/// the Microsoft.NET.HostModel bundler. The result is a standalone
/// executable (~70MB+ with .NET runtime).
///
/// NOTE: The Bundler API in the currently available NuGet package
/// (5.0.0-preview.1) doesn't directly expose AddFile/GenerateBundle.
/// This is deferred for now — the bundling can be done via
/// `dotnet publish /p:PublishSingleFile=true` instead.
/// </summary>
static async Task CreateBundleAsync(string dllPath, string outputDir)
{
    await Console.Error.WriteLineAsync("tosh: --publish-single-file requires 'dotnet publish' for full support (bundler API not available in current HostModel). Use: dotnet publish -c Release /p:PublishSingleFile=true /p:SelfContained=true");
}


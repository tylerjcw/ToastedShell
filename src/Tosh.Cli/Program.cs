using Tosh.Cli;
using Tosh.Core;
using Tosh.Language;

var runtime = ToshRuntime.CreateDefault(Console.Out, Console.Error);
var engine = new ToshEngine(runtime);
var diagnostics = new DiagnosticRenderer();

try
{
    await ToshStartupLoader.LoadAsync(engine);
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(diagnostics.Render(exception));
    Environment.ExitCode = 1;
    return;
}

if (args.Length > 0)
{
    if (args.Length == 1 && IsHelpSwitch(args[0]))
    {
        await PrintUsageAsync();
        return;
    }

    try
    {
        if (TryResolveScriptFile(args, runtime.CurrentDirectory, out var scriptPath, out var scriptArguments))
        {
            runtime.Variables["args"] = scriptArguments;
            await ExecuteFileAndPrintAsync(scriptPath);
        }
        else
        {
            await ExecuteAndPrintAsync(BuildScript(args));
        }
    }
    catch (Exception exception)
    {
        await Console.Error.WriteLineAsync(diagnostics.Render(exception));
        Environment.ExitCode = 1;
    }

    return;
}

var repl = new ToshRepl(engine);
await repl.RunAsync();
return;

async Task ExecuteAndPrintAsync(string source)
{
    runtime.RecordHistory(source);
    var sourceName = $"commandline #{runtime.History.Count}";
    var values = await engine.ExecuteToListAsync(source, sourceName);
    var rendered = runtime.Display.RenderMany(values, ConsoleDisplay.CreateRenderOptions(runtime));

    if (rendered.Length > 0)
    {
        await Console.Out.WriteLineAsync(rendered);
    }
}

async Task ExecuteFileAndPrintAsync(string path)
{
    var source = await File.ReadAllTextAsync(path);
    runtime.RecordHistory($"source {path}");
    var values = await engine.ExecuteToListAsync(source, path);
    var rendered = runtime.Display.RenderMany(values, ConsoleDisplay.CreateRenderOptions(runtime));

    if (rendered.Length > 0)
    {
        await Console.Out.WriteLineAsync(rendered);
    }
}

static string BuildScript(string[] arguments)
{
    if (arguments.Length == 1)
    {
        return arguments[0];
    }

    return string.Join(" ", arguments.Select(QuoteArgument));
}

static string QuoteArgument(string argument)
{
    if (argument.Length == 0 || argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '|' or '#'))
    {
        return $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    return argument;
}

static bool TryResolveScriptFile(string[] arguments, string currentDirectory, out string path, out string[] scriptArguments)
{
    path = string.Empty;
    scriptArguments = Array.Empty<string>();

    if (arguments.Length == 0)
    {
        return false;
    }

    var candidate = PathUtilities.ResolvePath(currentDirectory, arguments[0]);

    if (!File.Exists(candidate) || !string.Equals(Path.GetExtension(candidate), ".tosh", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    path = candidate;
    scriptArguments = arguments.Skip(1).ToArray();
    return true;
}

static bool IsHelpSwitch(string argument) => argument is "--help" or "-h";

static async Task PrintUsageAsync()
{
    await Console.Out.WriteLineAsync("Usage:");
    await Console.Out.WriteLineAsync("  tosh");
    await Console.Out.WriteLineAsync("  tosh 'echo hello | type-of'");
    await Console.Out.WriteLineAsync("  tosh script.tosh [args...]");
    await Console.Out.WriteLineAsync(string.Empty);
    await Console.Out.WriteLineAsync("Examples:");
    await Console.Out.WriteLineAsync("  tosh 'help'");
    await Console.Out.WriteLineAsync("  tosh 'help search json'");
    await Console.Out.WriteLineAsync("  tosh 'man where'");
    await Console.Out.WriteLineAsync("  tosh 'view detail'");
    await Console.Out.WriteLineAsync("  tosh 'ls -la'");
    await Console.Out.WriteLineAsync("  tosh 'echo \"Hello\".ToLower()'");
    await Console.Out.WriteLineAsync("  tosh 'echo String.Join(\" \", [\"Hello\", \"World\"])'");
    await Console.Out.WriteLineAsync("  tosh 'writeline \"hello\"'");
    await Console.Out.WriteLineAsync("  tosh 'mkdir -p scratch | get FullName'");
    await Console.Out.WriteLineAsync("  tosh 'alias ll = ls -la'");
    await Console.Out.WriteLineAsync("  tosh 'def recent(days: TimeSpan) { ls -la | where Modified > ((date now) - $days) }'");
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Tosh.Core;

namespace Tosh.Language;

internal sealed class ToshRuntimeNamespace
{
    private readonly ToshEngine _engine;

    public ToshRuntimeNamespace(ToshEngine engine)
    {
        _engine = engine;
        Last = new ToshLastNamespace(engine.Runtime);
        Script = new ToshScriptNamespace(engine);
        Function = new ToshFunctionNamespace(engine);
        Session = new ToshSessionNamespace(engine.Runtime);
        Host = new ToshHostNamespace();
    }

    public ToshConfig Config => _engine.Runtime.Config;

    public bool IsLoginShell => _engine.Runtime.IsLoginShell;

    public ToshLastNamespace Last { get; }

    public ToshScriptNamespace Script { get; }

    public ToshFunctionNamespace Function { get; }

    public ToshSessionNamespace Session { get; }

    public ToshHostNamespace Host { get; }
}

internal sealed class ToshLastNamespace
{
    private readonly ToshRuntime _runtime;

    public ToshLastNamespace(ToshRuntime runtime)
    {
        _runtime = runtime;
    }

    public object? Result => _runtime.LastResult;

    public int ExitCode => _runtime.LastExitCode;

    public TimeSpan? Duration => _runtime.LastCommandDuration;
}

internal sealed class ToshScriptNamespace
{
    private readonly ToshEngine _engine;

    public ToshScriptNamespace(ToshEngine engine)
    {
        _engine = engine;
    }

    public string Path => _engine.GetCurrentScriptPath();

    public string Name
    {
        get
        {
            var path = Path;
            return string.IsNullOrEmpty(path) ? string.Empty : System.IO.Path.GetFileName(path);
        }
    }

    public string Directory
    {
        get
        {
            var path = Path;

            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            return System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        }
    }

    public object?[] Args => _engine.GetCurrentScriptArguments().ToArray();
}

internal sealed class ToshFunctionNamespace
{
    private readonly ToshEngine _engine;

    public ToshFunctionNamespace(ToshEngine engine)
    {
        _engine = engine;
    }

    public string Name => _engine.GetCurrentFunctionName();

    public object?[] Args => _engine.GetCurrentFunctionArguments().ToArray();

    public object? Input => _engine.GetCurrentFunctionInput();
}

internal sealed class ToshSessionNamespace
{
    private readonly ToshRuntime _runtime;

    public ToshSessionNamespace(ToshRuntime runtime)
    {
        _runtime = runtime;
    }

    public string CurrentDirectory => _runtime.CurrentDirectory;

    public int HistoryCount => _runtime.History.Count;

    public long NextHistoryId => _runtime.NextHistoryId;

    public string HistoryFilePath => _runtime.Config.History.FilePath;

    public int JobCount => _runtime.GetJobs().Count;

    public int OpenHandleCount => ManagedFileHandle.GetOpenHandles().Count;

    public ManagedFileHandle[] OpenHandles => ManagedFileHandle.GetOpenHandles().ToArray();

    public StartupProfileData? StartupProfile => _runtime.StartupProfile;
}

internal sealed class ToshHostNamespace
{
    public string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                             ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                             ?? "0.0.0";

    public string RuntimeId => RuntimeInformation.RuntimeIdentifier;

    public string Framework => RuntimeInformation.FrameworkDescription;

    public string OSDescription => RuntimeInformation.OSDescription;

    public int ProcessId => Environment.ProcessId;

    public string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    public bool IsInteractive => !Console.IsInputRedirected;
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Tosh.Core;

namespace Tosh.Language;

internal sealed class ToshRuntimeNamespace
    : IShellRecordObject, IShellRuntimeNamespaceSummarySource
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

    public string ShellTypeName => "ToshRuntime";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Config):
                value = Config;
                return true;
            case nameof(IsLoginShell):
                value = IsLoginShell;
                return true;
            case nameof(Last):
                value = Last;
                return true;
            case nameof(Script):
                value = Script;
                return true;
            case nameof(Function):
                value = Function;
                return true;
            case nameof(Session):
                value = Session;
                return true;
            case nameof(Host):
                value = Host;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value)
    {
        return false;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new(nameof(Config), Config),
            new(nameof(IsLoginShell), IsLoginShell),
            new(nameof(Last), Last),
            new(nameof(Script), Script),
            new(nameof(Function), Function),
            new(nameof(Session), Session),
            new(nameof(Host), Host),
        ];
    }

    public RuntimeNamespaceDisplaySummary GetDisplaySummary()
    {
        var topLevel = new List<(string, string)>
        {
            ("$tosh.IsLoginShell", IsLoginShell ? "True" : "False"),
        };

        var sections = new List<RuntimeNamespaceSection>
        {
            new(
                "$tosh.Host",
                "Host Namespace",
                [
                    ("Version", Host.Version),
                    ("RuntimeId", Host.RuntimeId),
                    ("Framework", Host.Framework),
                    ("ProcessId", Host.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]),
            new(
                "$tosh.Session",
                "Session Namespace",
                [
                    ("CurrentDirectory", Session.CurrentDirectory),
                    ("JobCount", Session.JobCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("OpenHandleCount", Session.OpenHandleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("NextHistoryId", Session.NextHistoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]),
            new(
                "$tosh.Last",
                "Last Command Namespace",
                [
                    ("ExitCode", Last.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("Duration", Last.Duration?.ToString() ?? "n/a"),
                ]),
            new(
                "$tosh.Config",
                "Configuration Namespace",
                [
                    ("Config Dir", Config.Startup.RootDirectory),
                    ("Hist. File", Config.History.FilePath),
                    ("Hist. Max", Config.History.MaxEntries?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unlimited"),
                    ("Hist Dedup.", Config.History.Deduplication.ToString()),
                ]),
        };

        var hasScript = !string.IsNullOrEmpty(Script.Path);
        if (hasScript)
        {
            sections.Add(new RuntimeNamespaceSection(
                "$tosh.Script",
                "Script Namespace",
                [
                    ("Path", Script.Path),
                    ("Name", Script.Name),
                    ("Directory", Script.Directory),
                    ("Args", Script.Args.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]));
        }

        var hasFunction = !string.IsNullOrEmpty(Function.Name);
        if (hasFunction)
        {
            sections.Add(new RuntimeNamespaceSection(
                "$tosh.Function",
                "Function Namespace",
                [
                    ("Name", Function.Name),
                    ("Args", Function.Args.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]));
        }

        var footnotes = new List<string>();
        if (!hasScript)
        {
            footnotes.Add("$tosh.Script — only available inside a script context.");
        }
        if (!hasFunction)
        {
            footnotes.Add("$tosh.Function — only available inside a function context.");
        }
        footnotes.Add("Use '$tosh.<Member>' to drill in, or '$tosh | to json' for a full snapshot.");

        return new RuntimeNamespaceDisplaySummary(
            "$tosh | TōSh Live Runtime Namespace",
            ShellTypeName,
            topLevel,
            sections,
            footnotes);
    }
}

internal sealed class ToshLastNamespace
    : IShellRecordObject
{
    private readonly ToshRuntime _runtime;

    public ToshLastNamespace(ToshRuntime runtime)
    {
        _runtime = runtime;
    }

    public object? Result => _runtime.LastResult;

    public int ExitCode => _runtime.LastExitCode;

    public TimeSpan? Duration => _runtime.LastCommandDuration;

    public string ShellTypeName => "ToshRuntime.Last";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Result):
                value = Result;
                return true;
            case nameof(ExitCode):
                value = ExitCode;
                return true;
            case nameof(Duration):
                value = Duration;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Result), Result),
            new(nameof(ExitCode), ExitCode),
            new(nameof(Duration), Duration),
        ];
}

internal sealed class ToshScriptNamespace
    : IShellRecordObject
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

    public string ShellTypeName => "ToshRuntime.Script";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Path):
                value = Path;
                return true;
            case nameof(Name):
                value = Name;
                return true;
            case nameof(Directory):
                value = Directory;
                return true;
            case nameof(Args):
                value = Args;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Path), Path),
            new(nameof(Name), Name),
            new(nameof(Directory), Directory),
            new(nameof(Args), Args),
        ];
}

internal sealed class ToshFunctionNamespace
    : IShellRecordObject
{
    private readonly ToshEngine _engine;

    public ToshFunctionNamespace(ToshEngine engine)
    {
        _engine = engine;
    }

    public string Name => _engine.GetCurrentFunctionName();

    public object?[] Args => _engine.GetCurrentFunctionArguments().ToArray();

    public object? Input => _engine.GetCurrentFunctionInput();

    public string ShellTypeName => "ToshRuntime.Function";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Name):
                value = Name;
                return true;
            case nameof(Args):
                value = Args;
                return true;
            case nameof(Input):
                value = Input;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Name), Name),
            new(nameof(Args), Args),
            new(nameof(Input), Input),
        ];
}

internal sealed class ToshSessionNamespace
    : IShellRecordObject
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

    public string ShellTypeName => "ToshRuntime.Session";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(CurrentDirectory):
                value = CurrentDirectory;
                return true;
            case nameof(HistoryCount):
                value = HistoryCount;
                return true;
            case nameof(NextHistoryId):
                value = NextHistoryId;
                return true;
            case nameof(HistoryFilePath):
                value = HistoryFilePath;
                return true;
            case nameof(JobCount):
                value = JobCount;
                return true;
            case nameof(OpenHandleCount):
                value = OpenHandleCount;
                return true;
            case nameof(OpenHandles):
                value = OpenHandles;
                return true;
            case nameof(StartupProfile):
                value = StartupProfile;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(CurrentDirectory), CurrentDirectory),
            new(nameof(HistoryCount), HistoryCount),
            new(nameof(NextHistoryId), NextHistoryId),
            new(nameof(HistoryFilePath), HistoryFilePath),
            new(nameof(JobCount), JobCount),
            new(nameof(OpenHandleCount), OpenHandleCount),
            // OpenHandles can be large/noisy; expose count by default and keep handles addressable by name.
            new(nameof(StartupProfile), StartupProfile),
        ];
}

internal sealed class ToshHostNamespace
    : IShellRecordObject
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

    public string ShellTypeName => "ToshRuntime.Host";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Version):
                value = Version;
                return true;
            case nameof(RuntimeId):
                value = RuntimeId;
                return true;
            case nameof(Framework):
                value = Framework;
                return true;
            case nameof(OSDescription):
                value = OSDescription;
                return true;
            case nameof(ProcessId):
                value = ProcessId;
                return true;
            case nameof(ExecutablePath):
                value = ExecutablePath;
                return true;
            case nameof(IsInteractive):
                value = IsInteractive;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Version), Version),
            new(nameof(RuntimeId), RuntimeId),
            new(nameof(Framework), Framework),
            new(nameof(OSDescription), OSDescription),
            new(nameof(ProcessId), ProcessId),
            new(nameof(ExecutablePath), ExecutablePath),
            new(nameof(IsInteractive), IsInteractive),
        ];
}

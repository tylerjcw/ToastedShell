using Tosh.Core.Commands;

namespace Tosh.Core;

public sealed class ToshRuntime
{
    public ToshRuntime(TextWriter? output = null, TextWriter? error = null)
    {
        Output = output ?? TextWriter.Null;
        Error = error ?? TextWriter.Null;
        CurrentDirectory = Environment.CurrentDirectory;
        Commands = new ShellCommandRegistry();
        ObjectAccessor = new ReflectionObjectAccessor();
        TypeResolver = new DotNetTypeResolver();
        Invoker = new ReflectionInvoker();
        DisplayPreferences = new DisplayPreferences();
        DisplayProfiles = DisplayProfileRegistry.CreateDefault(DisplayPreferences);
        Formatter = new ObjectFormatter(DisplayProfiles);
        Display = new DisplayEngine(Formatter);
        Inspector = new ObjectInspector(Formatter);
        History = new List<CommandHistoryEntry>();
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        LoadedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SetLastExitCode(0);
    }

    public TextWriter Output { get; }

    public TextWriter Error { get; }

    public string CurrentDirectory { get; set; }

    public ShellCommandRegistry Commands { get; }

    public IObjectAccessor ObjectAccessor { get; }

    public ITypeResolver TypeResolver { get; }

    public ReflectionInvoker Invoker { get; }

    public DisplayPreferences DisplayPreferences { get; }

    public DisplayProfileRegistry DisplayProfiles { get; }

    public ObjectFormatter Formatter { get; }

    public DisplayEngine Display { get; }

    public ObjectInspector Inspector { get; }

    public IList<CommandHistoryEntry> History { get; }

    public IDictionary<string, object?> Variables { get; }

    public ISet<string> LoadedModules { get; }

    public IShellBlockExecutor? BlockExecutor { get; set; }

    public IShellEvaluator? Evaluator { get; set; }

    public bool ExitRequested { get; private set; }

    public int LastExitCode { get; private set; }

    public void RecordHistory(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        History.Add(new CommandHistoryEntry(History.Count + 1, source, DateTimeOffset.Now));
    }

    public void RequestExit()
    {
        ExitRequested = true;
    }

    public void SetLastExitCode(int exitCode)
    {
        LastExitCode = exitCode;
        Variables["LastExitCode"] = exitCode;
    }

    public static ToshRuntime CreateDefault(TextWriter? output = null, TextWriter? error = null)
    {
        var runtime = new ToshRuntime(output, error);
        BuiltInCommands.RegisterDefaults(runtime.Commands);
        return runtime;
    }
}

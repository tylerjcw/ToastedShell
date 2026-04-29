namespace Tosh.Language.Debugging;

/// <summary>
/// How the session should advance after being resumed.
/// </summary>
public enum DebugStepMode
{
    /// <summary>Run freely; only pause at breakpoints.</summary>
    Continue,
    /// <summary>Pause before the very next statement, regardless of depth.</summary>
    StepNext,
    /// <summary>Pause at the next statement at the same or shallower call depth (skip over function calls).</summary>
    StepOver,
    /// <summary>Pause at the next statement shallower than the current call depth (return from current function).</summary>
    StepOut,
}

/// <summary>
/// Instruction passed back to the hook when VS Code (or the CLI) resumes execution.
/// </summary>
public sealed class DebugResume
{
    public static readonly DebugResume Abort = new(DebugStepMode.Continue, aborted: true);

    public DebugStepMode Mode { get; }
    public bool Aborted { get; }

    public DebugResume(DebugStepMode mode, bool aborted = false)
    {
        Mode = mode;
        Aborted = aborted;
    }
}

/// <summary>
/// Shared debug session that manages breakpoints, step modes, and pause/resume coordination.
/// Used by both the DAP server and the interactive CLI debugger.
/// </summary>
public sealed class ToshDebugSession
{
    private readonly ToshEngine _engine;
    private readonly DebugHookDelegate? _previousHook;

    // Breakpoints: source name (normalized) → set of 1-based line numbers
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

    // Current step mode and target depth
    private volatile DebugStepMode _mode = DebugStepMode.StepNext;
    private int _targetDepth;

    // Pause/resume coordination
    private volatile TaskCompletionSource<DebugResume>? _pauseSource;

    // Snapshot of the context at the point where execution is paused (null when running)
    private volatile DebugStepContext? _pausedContext;

    public ToshDebugSession(ToshEngine engine, bool stopOnEntry = true)
    {
        _engine = engine;
        _previousHook = engine.DebugHook;
        _mode = stopOnEntry ? DebugStepMode.StepNext : DebugStepMode.Continue;
        engine.DebugHook = HookAsync;
    }

    /// <summary>The context of the statement where execution is currently paused. Null while running.</summary>
    public DebugStepContext? PausedContext => _pausedContext;

    /// <summary>True while execution is paused waiting for a resume command.</summary>
    public bool IsPaused => _pauseSource != null;

    // ── Breakpoints ───────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces all breakpoints for a given source file and returns the verified set.
    /// </summary>
    public IReadOnlySet<int> SetBreakpoints(string sourceName, IEnumerable<int> lines)
    {
        var key = NormalizeSource(sourceName);
        var set = new HashSet<int>(lines);
        if (set.Count > 0)
            _breakpoints[key] = set;
        else
            _breakpoints.Remove(key);
        return set;
    }

    public void ClearAllBreakpoints() => _breakpoints.Clear();

    private bool IsBreakpoint(string sourceName, int line)
    {
        var key = NormalizeSource(sourceName);
        return _breakpoints.TryGetValue(key, out var lines) && lines.Contains(line);
    }

    private static string NormalizeSource(string sourceName) =>
        Path.IsPathRooted(sourceName) ? sourceName : Path.GetFullPath(sourceName);

    // ── Pause / Resume ────────────────────────────────────────────────────────

    /// <summary>Resume running freely until the next breakpoint.</summary>
    public void Continue() => Dispatch(new DebugResume(DebugStepMode.Continue));

    /// <summary>Pause before the very next statement.</summary>
    public void StepNext() => Dispatch(new DebugResume(DebugStepMode.StepNext));

    /// <summary>Pause at the next statement at the same or shallower call depth.</summary>
    public void StepOver()
    {
        _targetDepth = _engine.CallStackDepth;
        Dispatch(new DebugResume(DebugStepMode.StepOver));
    }

    /// <summary>Pause at the next statement shallower than the current call depth.</summary>
    public void StepOut()
    {
        _targetDepth = _engine.CallStackDepth;
        Dispatch(new DebugResume(DebugStepMode.StepOut));
    }

    /// <summary>Abort the running script.</summary>
    public void Abort() => Dispatch(DebugResume.Abort);

    private void Dispatch(DebugResume resume)
    {
        _mode = resume.Mode;
        _pauseSource?.TrySetResult(resume);
    }

    // ── Hook ──────────────────────────────────────────────────────────────────

    private async Task<DebugAction> HookAsync(DebugStepContext ctx)
    {
        var depth = _engine.CallStackDepth;
        var line = ctx.Line ?? 0;
        var shouldPause = _mode switch
        {
            DebugStepMode.StepNext => true,
            DebugStepMode.StepOver => depth <= _targetDepth,
            DebugStepMode.StepOut  => depth < _targetDepth,
            DebugStepMode.Continue => line > 0 && IsBreakpoint(ctx.SourceName, line),
            _ => false
        };

        // Also pause at any breakpoint regardless of step mode
        if (!shouldPause && line > 0 && _mode != DebugStepMode.Continue)
            shouldPause = IsBreakpoint(ctx.SourceName, line);

        if (!shouldPause)
            return DebugAction.Continue;

        // Enter paused state
        _pausedContext = ctx;
        var tcs = new TaskCompletionSource<DebugResume>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pauseSource = tcs;

        await OnPausedAsync(ctx, depth);

        var resume = await tcs.Task;

        _pausedContext = null;
        _pauseSource = null;

        if (resume.Aborted)
            return DebugAction.Abort;

        _mode = resume.Mode;
        if (resume.Mode == DebugStepMode.StepOver)
            _targetDepth = depth;
        else if (resume.Mode == DebugStepMode.StepOut)
            _targetDepth = depth;

        return DebugAction.Continue;
    }

    // ── Extension Points ──────────────────────────────────────────────────────

    /// <summary>
    /// Called when execution pauses. Override or subscribe to notify consumers (DAP, CLI).
    /// </summary>
    public event Func<DebugStepContext, int, Task>? Paused;

    private async Task OnPausedAsync(DebugStepContext ctx, int depth)
    {
        var handler = Paused;
        if (handler != null)
            await handler(ctx, depth);
    }

    // ── Variables ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all visible variables formatted as display strings.
    /// </summary>
    public IReadOnlyList<(string Name, string Value, string Type)> GetFormattedVariables()
    {
        var raw = _engine.GetVisibleVariables();
        var result = new List<(string, string, string)>(raw.Count);
        foreach (var kv in raw)
        {
            var (display, type) = FormatVariable(kv.Value);
            result.Add((kv.Key, display, type));
        }
        return result;
    }

    private static (string display, string type) FormatVariable(object? value) =>
        value switch
        {
            null => ("null", "null"),
            bool b => (b ? "true" : "false", "bool"),
            string s => ($"\"{s}\"", "string"),
            int or long or short or byte or uint or ulong or ushort or sbyte => (value.ToString()!, "int"),
            double or float or decimal => (value.ToString()!, "float"),
            System.Collections.IList list => ($"[{list.Count} items]", "list"),
            System.Collections.IDictionary dict => ($"{{ {dict.Count} entries }}", "record"),
            _ => (value.ToString() ?? "null", value.GetType().Name)
        };

    // ── Call Stack ────────────────────────────────────────────────────────────

    public IReadOnlyList<string> GetCallStackNames() => _engine.GetCallStackNames();

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _engine.DebugHook = _previousHook;
        _pauseSource?.TrySetResult(DebugResume.Abort);
    }
}

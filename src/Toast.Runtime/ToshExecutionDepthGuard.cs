namespace Tosh.Runtime;

/// <summary>
/// Tracks nested ToastScript execution frames in the current asynchronous
/// flow and rejects unsafe recursion before the CLR stack is exhausted.
/// </summary>
public static class ToshExecutionDepthGuard
{
    /// <summary>
    /// The interpreter's limit on the stack a thread gets by default — `TOAST-0049`.
    /// </summary>
    /// <remarks>
    /// Stays deliberately below the first CLR stack-overflow boundary for the interpreter's
    /// heaviest path. Measured on 2026-08-22 by lifting the cap and bisecting
    /// out-of-process: a plain function and a class method both survive depth 250 and abort
    /// at 300, for the same boundary. 128 is therefore about half the real limit, which is
    /// the right shape for a guard — a body with more per-frame work reaches the floor
    /// sooner than the simple ones measured.
    /// </remarks>
    public const int MaximumSafeDepth = 128;

    /// <summary>
    /// The interpreter's limit on the stack this process actually has — `TOAST-0049`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling is a stack-size question: a Tōast frame costs a chain of async
    /// state-machine frames, so the depth that fits scales with the stack the threads were
    /// created with. <see cref="MaximumSafeDepth"/> is the answer for the 8 MB default;
    /// when <see cref="DeepStack.StackSizeVariable"/> gives the process more, holding it to
    /// that number would be reporting a limit that is no longer there.
    /// </para>
    /// <para>
    /// One frame per 64 KB, anchored on the measurement rather than fitted to it: 8 MB
    /// yields exactly the 128 that was measured for it, so the default is unchanged by
    /// construction. It is deliberately conservative — at 64 MB it allows 1,024 against a
    /// wall measured between 4,000 and 6,000.
    /// </para>
    /// </remarks>
    public static readonly int MaximumDepthForThisProcess =
        MaximumDepthForStack(DeepStack.ThreadStackBytes());

    /// <summary>The interpreter's limit on a stack of <paramref name="stackBytes"/>.</summary>
    /// <remarks>
    /// Separate from the field so the rule can be asserted at sizes this process does not
    /// have. <see cref="MaximumSafeDepth"/> is the floor as well as the anchor: a stack
    /// smaller than the default is not a reason to allow less than what was measured to be
    /// safe on it.
    /// </remarks>
    public static int MaximumDepthForStack(ulong stackBytes) => Math.Clamp(
        (int)Math.Min(stackBytes / BytesPerFrameAllowance, int.MaxValue),
        MaximumSafeDepth,
        MaximumCompiledDepth);

    /// <summary>
    /// Stack budgeted per Tōast frame, which is deliberately not the measured cost.
    /// </summary>
    /// <remarks>
    /// The measured cost is about 28 KB per frame at 8 MB and about 14 KB at 64 MB — it is
    /// not linear, because some of the stack is fixed overhead rather than per-frame. 64 KB
    /// is the number that makes 8 MB yield exactly the 128 already measured as safe for it,
    /// so the default limit is unchanged by construction and every larger stack is treated
    /// with at least the same caution.
    /// </remarks>
    private const ulong BytesPerFrameAllowance = 64UL * 1024;

    /// <summary>
    /// The highest limit any stack may buy — `TOAST-0049`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a per-backend number. Compiled code first got its own, on the strength of direct
    /// compiled recursion surviving depth 50,000 against the interpreter's 300 — but that
    /// is the cheapest compiled path, not the dearest. `new` on an emitted class is
    /// constructed through reflection, and bisecting it out-of-process on 2026-08-22 put
    /// its wall between depth 200 and 300: the same place the interpreter's is. A ceiling
    /// has to hold for the worst path on the backend, so both share one.
    /// </para>
    /// <para>
    /// This remains as the clamp on <see cref="MaximumDepthForStack"/>, so that an
    /// extravagant stack size cannot talk the guard into a limit nothing has been measured
    /// at.
    /// </para>
    /// </remarks>
    public const int MaximumCompiledDepth = 10_000;

    public static int DefaultMaximumDepth => MaximumDepthForThisProcess;

    private const int DiagnosticFrameLimit = 12;
    private static readonly AsyncLocal<ExecutionFrame?> s_currentFrame = new();

    public static int CurrentDepth => s_currentFrame.Value?.Depth ?? 0;

    public static IDisposable Enter(
        int maximumDepth,
        string frameName,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null)
    {
        ValidateMaximumDepth(maximumDepth);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameName);

        var parent = s_currentFrame.Value;
        var depth = (parent?.Depth ?? 0) + 1;
        if (depth > maximumDepth)
        {
            throw CreateLimitDiagnostic(
                maximumDepth,
                frameName,
                parent,
                sourceName,
                sourceText,
                span);
        }

        var frame = new ExecutionFrame(frameName, depth, parent);
        s_currentFrame.Value = frame;
        return new FrameLease(frame);
    }

    /// <summary>
    /// The absolute bound the guard will honour, which is the compiled limit.
    /// </summary>
    /// <remarks>
    /// Deliberately looser than <see cref="ValidateConfiguredDepth"/>. Compiled code asks
    /// for <see cref="MaximumCompiledDepth"/> and must be allowed it; a *script* setting
    /// `$tosh.Config.Shell.MaxRecursionDepth` is held to the interpreter's limit, because
    /// that is the stack it will actually run on.
    /// </remarks>
    public static void ValidateMaximumDepth(int maximumDepth)
    {
        if (maximumDepth is < 1 or > MaximumCompiledDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDepth),
                maximumDepth,
                $"ToastScript recursion depth must be between 1 and {MaximumCompiledDepth}.");
        }
    }

    /// <summary>What a script may configure, which is the interpreter's limit.</summary>
    public static void ValidateConfiguredDepth(int maximumDepth)
    {
        if (maximumDepth < 1 || maximumDepth > MaximumDepthForThisProcess)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDepth),
                maximumDepth,
                $"ToastScript recursion depth must be between 1 and {MaximumDepthForThisProcess}.");
        }
    }

    private static ToshDiagnosticException CreateLimitDiagnostic(
        int maximumDepth,
        string frameName,
        ExecutionFrame? parent,
        string? sourceName,
        string? sourceText,
        TextSpan? span)
    {
        var names = new List<string>(DiagnosticFrameLimit) { frameName };
        for (var frame = parent;
             frame is not null && names.Count < DiagnosticFrameLimit;
             frame = frame.Parent)
        {
            names.Add(frame.Name);
        }

        var omittedCount = Math.Max(0, (parent?.Depth ?? 0) + 1 - names.Count);
        var stackSummary = string.Join(" → ", names);
        if (omittedCount > 0)
        {
            stackSummary += $" → … ({omittedCount} older frame(s))";
        }

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.recursion_limit_exceeded",
            Title: "Maximum ToastScript recursion depth was exceeded.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label:
                $"entering '{frameName}' would exceed the configured limit of {maximumDepth}",
            Help: "Reduce the recursion or rewrite it as a loop. Configure $tosh.Config.Shell.MaxRecursionDepth to choose a stricter limit.",
            Info:
                $"Configured limit: {maximumDepth}. " +
                $"Active ToastScript frames, innermost first: {stackSummary}"));
    }

    private sealed record ExecutionFrame(
        string Name,
        int Depth,
        ExecutionFrame? Parent);

    private sealed class FrameLease(ExecutionFrame frame) : IDisposable
    {
        private ExecutionFrame? _frame = frame;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _frame, null);
            if (current is not null && ReferenceEquals(s_currentFrame.Value, current))
            {
                s_currentFrame.Value = current.Parent;
            }
        }
    }
}

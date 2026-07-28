namespace Tosh.Runtime;

/// <summary>
/// Tracks nested ToastScript execution frames in the current asynchronous
/// flow and rejects unsafe recursion before the CLR stack is exhausted.
/// </summary>
public static class ToshExecutionDepthGuard
{
    /// <summary>
    /// Default and highest supported recursion limit. This stays
    /// deliberately below the first observed CLR stack-overflow boundary
    /// for the interpreter's heaviest class-dispatch path.
    /// </summary>
    public const int MaximumSafeDepth = 128;

    public const int DefaultMaximumDepth = MaximumSafeDepth;

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

    public static void ValidateMaximumDepth(int maximumDepth)
    {
        if (maximumDepth is < 1 or > MaximumSafeDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDepth),
                maximumDepth,
                $"ToastScript recursion depth must be between 1 and {MaximumSafeDepth}.");
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

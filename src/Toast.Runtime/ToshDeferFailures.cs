using System.Runtime.ExceptionServices;

namespace Tosh.Runtime;

/// <summary>
/// Preserves every failure observed while a ToastScript scope unwinds its
/// deferred cleanup blocks.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BodyFailure"/> is the exception that caused the scope to exit,
/// when there was one. <see cref="CleanupFailures"/> follows actual cleanup
/// execution order, which is reverse registration (LIFO) order.
/// </para>
/// <para>
/// A single failure is never wrapped in this type. The runtime uses this
/// aggregate only when at least two failures compete for propagation.
/// </para>
/// </remarks>
public sealed class ToshDeferAggregateException : AggregateException
{
    private readonly IReadOnlyList<Exception> _failures;
    private readonly IReadOnlyList<Exception> _cleanupFailures;

    /// <summary>
    /// Creates an inspectable defer aggregate. Runtime-generated instances
    /// are normally created through <see cref="ToshDeferFailureState"/>.
    /// </summary>
    public ToshDeferAggregateException(
        Exception? bodyFailure,
        IReadOnlyList<Exception> cleanupFailures)
        : this(CreateSnapshot(bodyFailure, cleanupFailures), owner: new object())
    {
    }

    internal ToshDeferAggregateException(
        Exception? bodyFailure,
        IReadOnlyList<Exception> cleanupFailures,
        object owner)
        : this(CreateSnapshot(bodyFailure, cleanupFailures), owner)
    {
    }

    private ToshDeferAggregateException(FailureSnapshot snapshot, object owner)
        : base(snapshot.Message, snapshot.Failures)
    {
        ArgumentNullException.ThrowIfNull(owner);

        BodyFailure = snapshot.BodyFailure;
        _cleanupFailures = Array.AsReadOnly(snapshot.CleanupFailures);
        _failures = Array.AsReadOnly(snapshot.Failures);
        Owner = owner;
    }

    /// <summary>The exception raised by the scope body, if any.</summary>
    public Exception? BodyFailure { get; }

    /// <summary>
    /// Cleanup exceptions in actual execution order (newest defer first).
    /// </summary>
    public IReadOnlyList<Exception> CleanupFailures => _cleanupFailures;

    /// <summary>
    /// All failures in deterministic order: body first, then cleanup failures.
    /// </summary>
    public IReadOnlyList<Exception> Failures => _failures;

    internal object Owner { get; }

    private static FailureSnapshot CreateSnapshot(
        Exception? bodyFailure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(cleanupFailures);

        // Public callers may supply a mutable or side-effecting
        // IReadOnlyList. Enumerate it once so AggregateException's
        // InnerExceptions and the defer-specific views cannot diverge.
        var cleanupSnapshot = cleanupFailures.ToArray();
        if (cleanupSnapshot.Length == 0)
        {
            throw new ArgumentException(
                "A defer aggregate requires at least one cleanup failure.",
                nameof(cleanupFailures));
        }
        if (bodyFailure is null && cleanupSnapshot.Length == 1)
        {
            throw new ArgumentException(
                "A defer aggregate requires at least two competing failures.",
                nameof(cleanupFailures));
        }

        for (var index = 0; index < cleanupSnapshot.Length; index++)
        {
            if (cleanupSnapshot[index] is null)
            {
                throw new ArgumentException(
                    "Cleanup failure collections cannot contain null.",
                    nameof(cleanupFailures));
            }
        }

        var failures = new Exception[
            cleanupSnapshot.Length + (bodyFailure is null ? 0 : 1)];
        var offset = 0;
        if (bodyFailure is not null)
        {
            failures[0] = bodyFailure;
            offset = 1;
        }

        for (var index = 0; index < cleanupSnapshot.Length; index++)
        {
            failures[index + offset] = cleanupSnapshot[index];
        }

        var message = bodyFailure is null
            ? $"{cleanupSnapshot.Length} deferred cleanups failed."
            : $"The scope body failed and {cleanupSnapshot.Length} deferred cleanup(s) also failed.";
        return new FailureSnapshot(
            bodyFailure,
            cleanupSnapshot,
            failures,
            message);
    }

    private sealed record FailureSnapshot(
        Exception? BodyFailure,
        Exception[] CleanupFailures,
        Exception[] Failures,
        string Message);
}

/// <summary>
/// Collects failures for one defer-aware scope. This public, deliberately
/// small API is also called by generated IL.
/// </summary>
public sealed class ToshDeferFailureState
{
    private readonly object _owner = new();
    private readonly List<Exception> _cleanupFailures = [];
    private Exception? _bodyFailure;

    /// <summary>Records the exception propagating out of the scope body.</summary>
    public void CaptureBodyFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure is ToshDeferAggregateException aggregate)
        {
            if (ReferenceEquals(aggregate.Owner, _owner))
            {
                return;
            }

            CaptureFlattenedBody(aggregate.BodyFailure);
            _cleanupFailures.AddRange(aggregate.CleanupFailures);
            return;
        }

        if (ToshDeferFailures.TryGetAttachedFailure(
                failure,
                out var owner,
                out var attachedCleanupFailures))
        {
            if (ReferenceEquals(owner, _owner))
            {
                return;
            }

            // A sole cleanup failure is rethrown unchanged and therefore
            // contains itself in its attached cleanup list. Cancellation
            // with cleanup failures keeps the OCE as the body and attaches
            // only the competing cleanup exceptions.
            if (!attachedCleanupFailures.Any(
                    cleanup => ReferenceEquals(cleanup, failure)))
            {
                CaptureFlattenedBody(failure);
            }

            _cleanupFailures.AddRange(attachedCleanupFailures);
            return;
        }

        if (ReferenceEquals(_bodyFailure, failure) ||
            _cleanupFailures.Any(cleanup => ReferenceEquals(cleanup, failure)))
        {
            return;
        }

        CaptureFlattenedBody(failure);
    }

    /// <summary>
    /// Records one cleanup invocation's failure. Dedicated nested defer
    /// aggregates are flattened; arbitrary aggregate exceptions are retained.
    /// </summary>
    public void CaptureCleanupFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure is ToshDeferAggregateException aggregate)
        {
            if (ReferenceEquals(aggregate.Owner, _owner))
            {
                return;
            }

            _cleanupFailures.AddRange(aggregate.Failures);
            return;
        }

        if (ToshDeferFailures.TryGetAttachedFailure(
                failure,
                out var owner,
                out var attachedCleanupFailures))
        {
            if (ReferenceEquals(owner, _owner))
            {
                return;
            }

            if (attachedCleanupFailures.Any(
                    cleanup => ReferenceEquals(cleanup, failure)))
            {
                _cleanupFailures.AddRange(attachedCleanupFailures);
            }
            else
            {
                // This is normally an OperationCanceledException that was a
                // nested cleanup scope's body failure.
                _cleanupFailures.Add(failure);
                _cleanupFailures.AddRange(attachedCleanupFailures);
            }

            return;
        }

        _cleanupFailures.Add(failure);
    }

    /// <summary>
    /// Throws only when cleanup failed. With one cleanup failure and no body
    /// failure, the exact cleanup exception is rethrown. Competing failures
    /// become <see cref="ToshDeferAggregateException"/>.
    /// </summary>
    public void ThrowIfCleanupFailed()
    {
        if (_cleanupFailures.Count == 0)
        {
            return;
        }

        if (_bodyFailure is OperationCanceledException cancellation)
        {
            ToshDeferFailures.AttachFailure(
                cancellation,
                _owner,
                _cleanupFailures);
            ExceptionDispatchInfo.Capture(cancellation).Throw();
        }

        if (_bodyFailure is null && _cleanupFailures.Count == 1)
        {
            var soleFailure = _cleanupFailures[0];
            ToshDeferFailures.AttachFailure(
                soleFailure,
                _owner,
                [soleFailure]);
            ExceptionDispatchInfo.Capture(soleFailure).Throw();
        }

        throw new ToshDeferAggregateException(
            _bodyFailure,
            _cleanupFailures,
            _owner);
    }

    private void CaptureFlattenedBody(Exception? failure)
    {
        if (failure is null)
        {
            return;
        }

        if (_bodyFailure is null)
        {
            _bodyFailure = failure;
            return;
        }

        if (!ReferenceEquals(_bodyFailure, failure))
        {
            // This path is defensive: generated and interpreted unwinders
            // capture one body exit, but retaining an unexpected later cause
            // is safer than silently replacing it.
            _cleanupFailures.Add(failure);
        }
    }
}

/// <summary>Inspection and diagnostic helpers for defer-unwind failures.</summary>
public static class ToshDeferFailures
{
    /// <summary>
    /// Exception.Data key containing the ordered cleanup failures attached to
    /// an otherwise unchanged sole exception or cancellation exception.
    /// </summary>
    public const string CleanupFailuresDataKey = "Tosh.Runtime.Defer.CleanupFailures";

    private const string OwnerDataKey = "Tosh.Runtime.Defer.FailureOwner";
    private const string SourceContextDataKey = "Tosh.Runtime.Defer.SourceContext";

    /// <summary>True when <paramref name="exception"/> came from defer unwind.</summary>
    public static bool IsDeferFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ToshDeferAggregateException ||
               TryGetAttachedFailure(
                   exception,
                   out _,
                   out _);
    }

    /// <summary>
    /// Returns cleanup failures in actual LIFO execution order. The result is
    /// empty when the exception has no attached defer-cleanup context.
    /// </summary>
    public static IReadOnlyList<Exception> GetCleanupFailures(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is ToshDeferAggregateException aggregate)
        {
            return aggregate.CleanupFailures;
        }

        return TryGetAttachedFailure(
            exception,
            out _,
            out var failures)
                ? failures
                : Array.Empty<Exception>();
    }

    /// <summary>
    /// Converts a defer failure into ordered diagnostics suitable for CLI,
    /// MCP, text, and NDJSON rendering.
    /// </summary>
    public static ToshDiagnosticException ToDiagnosticException(Exception exception)
        => ToDiagnosticException(exception, sourceName: null, sourceText: null);

    /// <summary>
    /// Converts a defer failure into ordered diagnostics, filling source
    /// context when an underlying exception does not already carry it.
    /// </summary>
    public static ToshDiagnosticException ToDiagnosticException(
        Exception exception,
        string? sourceName,
        string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Exception? bodyFailure;
        IReadOnlyList<Exception> cleanupFailures;

        if (exception is ToshDeferAggregateException aggregate)
        {
            bodyFailure = aggregate.BodyFailure;
            cleanupFailures = aggregate.CleanupFailures;
        }
        else
        {
            cleanupFailures = GetCleanupFailures(exception);
            if (cleanupFailures.Count == 0)
            {
                throw new ArgumentException(
                    "The exception does not contain defer failure information.",
                    nameof(exception));
            }

            bodyFailure = cleanupFailures.Any(
                cleanup => ReferenceEquals(cleanup, exception))
                ? null
                : exception;
        }

        var diagnostics = new List<ToshDiagnostic>();
        if (bodyFailure is not null)
        {
            AddBodyDiagnostics(
                diagnostics,
                bodyFailure,
                sourceName,
                sourceText);
        }

        for (var index = 0; index < cleanupFailures.Count; index++)
        {
            AddCleanupDiagnostics(
                diagnostics,
                cleanupFailures[index],
                index + 1,
                sourceName,
                sourceText);
        }

        return new ToshDiagnosticException(diagnostics);
    }

    internal static void AttachFailure(
        Exception exception,
        object owner,
        IReadOnlyList<Exception> cleanupFailures)
    {
        var snapshot = cleanupFailures.ToArray();
        exception.Data[CleanupFailuresDataKey] = Array.AsReadOnly(snapshot);
        exception.Data[OwnerDataKey] = owner;
    }

    internal static void AttachSourceContext(
        Exception exception,
        string sourceName,
        string sourceText)
    {
        if (!exception.Data.Contains(SourceContextDataKey))
        {
            exception.Data[SourceContextDataKey] =
                new DeferSourceContext(sourceName, sourceText);
        }
    }

    internal static bool TryGetAttachedFailure(
        Exception exception,
        out object? owner,
        out IReadOnlyList<Exception> cleanupFailures)
    {
        if (exception.Data[CleanupFailuresDataKey] is IReadOnlyList<Exception> failures)
        {
            var snapshot = failures.ToArray();
            if (snapshot.Length == 0 ||
                snapshot.Any(static failure => failure is null))
            {
                owner = null;
                cleanupFailures = Array.Empty<Exception>();
                return false;
            }

            owner = exception.Data[OwnerDataKey];
            cleanupFailures = Array.AsReadOnly(snapshot);
            return true;
        }

        owner = null;
        cleanupFailures = Array.Empty<Exception>();
        return false;
    }

    private static void AddBodyDiagnostics(
        List<ToshDiagnostic> diagnostics,
        Exception failure,
        string? sourceName,
        string? sourceText)
    {
        (sourceName, sourceText) = ResolveSourceContext(
            failure,
            sourceName,
            sourceText);

        if (failure is ToshDiagnosticException diagnostic &&
            diagnostic.Diagnostics.Count > 0)
        {
            diagnostics.AddRange(diagnostic.Diagnostics.Select(item => item with
            {
                SourceName = item.SourceName ?? sourceName,
                SourceText = item.SourceText ?? sourceText,
            }));
            return;
        }

        diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.runtime.defer_body_failed",
            Title: $"Deferred scope body failed: {failure.Message}",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: failure is ThrowSignalException signal ? signal.Span : null,
            Label: "the scope failed before deferred cleanup completed"));
    }

    private static void AddCleanupDiagnostics(
        List<ToshDiagnostic> diagnostics,
        Exception failure,
        int cleanupIndex,
        string? sourceName,
        string? sourceText)
    {
        (sourceName, sourceText) = ResolveSourceContext(
            failure,
            sourceName,
            sourceText);

        if (failure is ToshDiagnosticException diagnostic &&
            diagnostic.Diagnostics.Count > 0)
        {
            foreach (var item in diagnostic.Diagnostics)
            {
                var originalCode = string.IsNullOrWhiteSpace(item.Code)
                    ? null
                    : $"Original diagnostic: {item.Code}.";
                diagnostics.Add(item with
                {
                    Code = "tosh.runtime.defer_cleanup_failed",
                    Title = $"Deferred cleanup #{cleanupIndex} failed: {item.Title}",
                    SourceName = item.SourceName ?? sourceName,
                    SourceText = item.SourceText ?? sourceText,
                    Info = JoinInfo(item.Info, originalCode),
                });
            }

            return;
        }

        diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.runtime.defer_cleanup_failed",
            Title: $"Deferred cleanup #{cleanupIndex} failed: {failure.Message}",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: failure is ThrowSignalException signal ? signal.Span : null,
            Label: "this deferred cleanup failed while the scope was unwinding"));
    }

    private static string? JoinInfo(string? existing, string? addition)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return addition;
        }

        if (string.IsNullOrWhiteSpace(addition))
        {
            return existing;
        }

        return $"{existing} {addition}";
    }

    private static (string? SourceName, string? SourceText) ResolveSourceContext(
        Exception failure,
        string? fallbackSourceName,
        string? fallbackSourceText)
    {
        return failure.Data[SourceContextDataKey] is DeferSourceContext context
            ? (context.SourceName, context.SourceText)
            : (fallbackSourceName, fallbackSourceText);
    }

    private sealed record DeferSourceContext(
        string SourceName,
        string SourceText);
}

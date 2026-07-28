using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class ToshDeferFailureTests
{
    [Fact]
    public void Aggregate_constructor_rejects_a_single_failure()
    {
        var failure = new InvalidOperationException("cleanup");

        var thrown = Assert.Throws<ArgumentException>(
            () => new ToshDeferAggregateException(
                bodyFailure: null,
                cleanupFailures: [failure]));

        Assert.Equal("cleanupFailures", thrown.ParamName);
    }

    [Fact]
    public void Aggregate_constructor_snapshots_cleanup_failures_once()
    {
        var indexed = new[]
        {
            new InvalidOperationException("indexed-A"),
            new InvalidOperationException("indexed-B"),
        };
        var enumerated = new[]
        {
            new InvalidOperationException("enumerated-A"),
            new InvalidOperationException("enumerated-B"),
        };

        var aggregate = new ToshDeferAggregateException(
            bodyFailure: null,
            cleanupFailures: new DivergentFailureList(indexed, enumerated));

        Assert.Equal(enumerated, aggregate.CleanupFailures);
        Assert.Equal(aggregate.Failures, aggregate.InnerExceptions);
    }

    [Fact]
    public void Sole_cleanup_failure_is_rethrown_unchanged()
    {
        var failure = new InvalidOperationException("cleanup");
        var state = new ToshDeferFailureState();
        state.CaptureCleanupFailure(failure);

        var thrown = Assert.Throws<InvalidOperationException>(
            state.ThrowIfCleanupFailed);

        Assert.Same(failure, thrown);
        Assert.True(ToshDeferFailures.IsDeferFailure(thrown));
        Assert.Equal([failure], ToshDeferFailures.GetCleanupFailures(thrown));
    }

    [Fact]
    public void Same_state_repropagation_does_not_duplicate_cleanup_failures()
    {
        var sole = new InvalidOperationException("sole");
        var soleState = new ToshDeferFailureState();
        soleState.CaptureCleanupFailure(sole);
        var firstSoleThrow = Assert.Throws<InvalidOperationException>(
            soleState.ThrowIfCleanupFailed);

        soleState.CaptureCleanupFailure(firstSoleThrow);
        var secondSoleThrow = Assert.Throws<InvalidOperationException>(
            soleState.ThrowIfCleanupFailed);

        Assert.Same(sole, secondSoleThrow);
        Assert.Equal([sole], ToshDeferFailures.GetCleanupFailures(secondSoleThrow));

        var newest = new InvalidOperationException("newest");
        var oldest = new InvalidOperationException("oldest");
        var aggregateState = new ToshDeferFailureState();
        aggregateState.CaptureCleanupFailure(newest);
        aggregateState.CaptureCleanupFailure(oldest);
        var firstAggregate = Assert.Throws<ToshDeferAggregateException>(
            aggregateState.ThrowIfCleanupFailed);

        aggregateState.CaptureCleanupFailure(firstAggregate);
        var secondAggregate = Assert.Throws<ToshDeferAggregateException>(
            aggregateState.ThrowIfCleanupFailed);

        Assert.Equal([newest, oldest], secondAggregate.CleanupFailures);
    }

    [Fact]
    public void Body_and_cleanup_failures_form_an_ordered_aggregate()
    {
        var body = new InvalidOperationException("body");
        var newest = new InvalidOperationException("newest");
        var oldest = new InvalidOperationException("oldest");
        var state = new ToshDeferFailureState();
        state.CaptureBodyFailure(body);
        state.CaptureCleanupFailure(newest);
        state.CaptureCleanupFailure(oldest);

        var aggregate = Assert.Throws<ToshDeferAggregateException>(
            state.ThrowIfCleanupFailed);

        Assert.Same(body, aggregate.BodyFailure);
        Assert.Equal([newest, oldest], aggregate.CleanupFailures);
        Assert.Equal([body, newest, oldest], aggregate.Failures);
        Assert.Equal(aggregate.Failures, aggregate.InnerExceptions);
    }

    [Fact]
    public void Nested_defer_aggregate_flattens_without_reclassifying_cleanup()
    {
        var newest = new InvalidOperationException("newest");
        var middle = new InvalidOperationException("middle");
        var oldest = new InvalidOperationException("oldest");

        var inner = new ToshDeferFailureState();
        inner.CaptureCleanupFailure(newest);
        inner.CaptureCleanupFailure(middle);
        var innerAggregate = Assert.Throws<ToshDeferAggregateException>(
            inner.ThrowIfCleanupFailed);

        var outer = new ToshDeferFailureState();
        outer.CaptureBodyFailure(innerAggregate);
        outer.CaptureCleanupFailure(oldest);
        var aggregate = Assert.Throws<ToshDeferAggregateException>(
            outer.ThrowIfCleanupFailed);

        Assert.Null(aggregate.BodyFailure);
        Assert.Equal([newest, middle, oldest], aggregate.CleanupFailures);
        Assert.Equal(aggregate.CleanupFailures, aggregate.Failures);
    }

    [Fact]
    public void Nested_sole_cleanup_failure_retains_cleanup_classification()
    {
        var newest = new InvalidOperationException("newest");
        var oldest = new InvalidOperationException("oldest");

        var inner = new ToshDeferFailureState();
        inner.CaptureCleanupFailure(newest);
        var propagated = Assert.Throws<InvalidOperationException>(
            inner.ThrowIfCleanupFailed);

        var outer = new ToshDeferFailureState();
        outer.CaptureBodyFailure(propagated);
        outer.CaptureCleanupFailure(oldest);
        var aggregate = Assert.Throws<ToshDeferAggregateException>(
            outer.ThrowIfCleanupFailed);

        Assert.Null(aggregate.BodyFailure);
        Assert.Equal([newest, oldest], aggregate.CleanupFailures);
    }

    [Fact]
    public void Cancellation_remains_the_outward_exception_and_carries_cleanup_failures()
    {
        var cancellation = new OperationCanceledException("cancelled");
        var cleanup = new InvalidOperationException("cleanup");
        var state = new ToshDeferFailureState();
        state.CaptureBodyFailure(cancellation);
        state.CaptureCleanupFailure(cleanup);

        var thrown = Assert.Throws<OperationCanceledException>(
            state.ThrowIfCleanupFailed);

        Assert.Same(cancellation, thrown);
        Assert.Equal([cleanup], ToshDeferFailures.GetCleanupFailures(thrown));
    }

    [Fact]
    public void Diagnostic_conversion_preserves_body_then_tags_cleanup_failures()
    {
        var body = ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.body_problem",
            Title: "body"));
        var cleanup = ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.cleanup_problem",
            Title: "cleanup"));
        var state = new ToshDeferFailureState();
        state.CaptureBodyFailure(body);
        state.CaptureCleanupFailure(cleanup);
        var aggregate = Assert.Throws<ToshDeferAggregateException>(
            state.ThrowIfCleanupFailed);

        var diagnostic = ToshDeferFailures.ToDiagnosticException(aggregate);

        Assert.Equal(
            ["tosh.runtime.body_problem", "tosh.runtime.defer_cleanup_failed"],
            diagnostic.Diagnostics.Select(item => item.Code).ToArray());
        Assert.Contains(
            "tosh.runtime.cleanup_problem",
            diagnostic.Diagnostics[1].Info,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_nested_diagnostics_receive_stable_defer_fallbacks()
    {
        var state = new ToshDeferFailureState();
        state.CaptureBodyFailure(new ToshDiagnosticException([]));
        state.CaptureCleanupFailure(new ToshDiagnosticException([]));
        var aggregate = Assert.Throws<ToshDeferAggregateException>(
            state.ThrowIfCleanupFailed);

        var diagnostic = ToshDeferFailures.ToDiagnosticException(aggregate);

        Assert.Equal(
            [
                "tosh.runtime.defer_body_failed",
                "tosh.runtime.defer_cleanup_failed",
            ],
            diagnostic.Diagnostics.Select(item => item.Code).ToArray());
    }

    [Fact]
    public void Empty_external_cleanup_metadata_is_not_treated_as_a_defer_failure()
    {
        var failure = new InvalidOperationException("ordinary");
        failure.Data[ToshDeferFailures.CleanupFailuresDataKey] =
            Array.Empty<Exception>();

        Assert.False(ToshDeferFailures.IsDeferFailure(failure));
        Assert.Empty(ToshDeferFailures.GetCleanupFailures(failure));

        var rendered = new DiagnosticRenderer(
            theme: null,
            config: null,
            forcePlain: true).Render(failure);
        Assert.Contains("tosh.runtime.error", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_expands_every_defer_failure()
    {
        var state = new ToshDeferFailureState();
        state.CaptureBodyFailure(new InvalidOperationException("body"));
        state.CaptureCleanupFailure(new InvalidOperationException("cleanup"));
        var aggregate = Assert.Throws<ToshDeferAggregateException>(
            state.ThrowIfCleanupFailed);

        var rendered = new DiagnosticRenderer(
            theme: null,
            config: null,
            forcePlain: true).Render(aggregate);

        Assert.Contains("tosh.runtime.defer_body_failed", rendered, StringComparison.Ordinal);
        Assert.Contains("tosh.runtime.defer_cleanup_failed", rendered, StringComparison.Ordinal);
        Assert.Contains("body", rendered, StringComparison.Ordinal);
        Assert.Contains("cleanup", rendered, StringComparison.Ordinal);
    }

    private sealed class DivergentFailureList(
        IReadOnlyList<Exception> indexed,
        IReadOnlyList<Exception> enumerated) : IReadOnlyList<Exception>
    {
        public int Count => indexed.Count;

        public Exception this[int index] => indexed[index];

        public IEnumerator<Exception> GetEnumerator()
            => enumerated.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
}

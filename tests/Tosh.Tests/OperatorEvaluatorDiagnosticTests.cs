using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class OperatorEvaluatorDiagnosticTests
{
    [Fact]
    public void Binary_diagnostic_wrapper_preserves_language_protocol_failures()
    {
        var marked = new InvalidOperationException("marked");
        marked.Data["tosh.thrown"] = true;

        Exception[] failures =
        [
            ToshDiagnosticException.Create(new ToshDiagnostic(
                "tosh.test",
                "diagnostic",
                "source.tosh",
                "1 + 2",
                new TextSpan(0, 5))),
            new OperationCanceledException("cancelled"),
            new ReturnSignalException(default, [42]),
            new ThrowSignalException(default, "boom"),
            new ToshDeferAggregateException(
                new InvalidOperationException("body"),
                [new InvalidOperationException("cleanup")]),
            marked,
        ];

        foreach (var failure in failures)
        {
            var receiver = new ThrowingOperator(failure);
            var actual = Record.Exception(
                () => OperatorEvaluator.EvaluateBinaryWithDiagnostics(
                    receiver,
                    "+",
                    1,
                    "source.tosh",
                    "receiver + 1",
                    0,
                    12));

            Assert.Same(failure, actual);
        }
    }

    [Fact]
    public void Binary_diagnostic_wrapper_wraps_ordinary_failures_with_source_context()
    {
        var receiver = new ThrowingOperator(
            new InvalidOperationException("ordinary failure"));

        var exception = Assert.Throws<ToshDiagnosticException>(
            () => OperatorEvaluator.EvaluateBinaryWithDiagnostics(
                receiver,
                "+",
                1,
                "source.tosh",
                "receiver + 1",
                3,
                8));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.expression_failed", diagnostic.Code);
        Assert.Equal("ordinary failure", diagnostic.Title);
        Assert.Equal("source.tosh", diagnostic.SourceName);
        Assert.Equal("receiver + 1", diagnostic.SourceText);
        Assert.Equal(new TextSpan(3, 8), diagnostic.Span);
        Assert.Equal("while evaluating this expression", diagnostic.Label);
    }

    [ToshType("class", 0, 0)]
    private sealed class ThrowingOperator(Exception failure)
    {
        [ToshOriginalName("+")]
        private object? Evaluate(object? other)
        {
            _ = other;
            throw failure;
        }
    }
}

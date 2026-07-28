using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-24 drift guard. Annotated conversion is reachable through both a
/// synchronous and an asynchronous path, because refinement
/// <c>where</c>/<c>coerce</c> clauses run user code while the surrounding
/// type resolution is pure. This runs one corpus through both and asserts
/// they agree, so a future semantic change cannot land on one surface and
/// silently miss the other — which is exactly how the equality rule in
/// `TS-P1-14` initially went in.
/// </summary>
public sealed class AnnotatedConversionParityTests
{
    /// <summary>
    /// Annotation plus value pairs spanning the branches of the
    /// conversion logic: primitives, widening, string parsing, failures,
    /// nullable annotations, collections, and trait-constraint names.
    /// </summary>
    public static IEnumerable<object[]> Corpus()
    {
        var cases = new (string Annotation, object? Value)[]
        {
            ("int", 42),
            ("int", "42"),
            ("int", "nope"),
            ("int", null),
            ("int?", null),
            ("int", 42L),
            ("int", 4.0),
            ("long", 42),
            ("double", 1),
            ("double", "2.5"),
            ("bool", true),
            ("bool", "true"),
            ("bool", "notabool"),
            ("string", 42),
            ("string", "already"),
            ("string?", null),
            ("Numeric", 42),
            ("Numeric", "text"),
            ("Comparable", 42),
            ("list", new object?[] { 1, 2 }),
            ("NoSuchTypeAtAll", 42),
            ("int", new object?[] { 1, 2 }),
        };

        foreach (var (annotation, value) in cases)
        {
            yield return [annotation, value!];
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task Sync_and_async_annotated_conversion_agree(string annotation, object? value)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var syncSucceeded = engine.TryConvertAnnotatedValue(annotation, value, out var syncConverted);
        var asyncResult = await engine.TryConvertAnnotatedValueAsync(annotation, value, CancellationToken.None);

        Assert.Equal(syncSucceeded, asyncResult.Success);
        Assert.Equal(Describe(syncConverted), Describe(asyncResult.Converted));
    }

    [Theory]
    [InlineData("Port", 8080, true)]
    [InlineData("Port", 99999, true)]
    [InlineData("Port", 0, true)]
    [InlineData("Port", "8080", true)]
    [InlineData("Positive", 5, true)]
    [InlineData("Positive", -5, false)]
    public async Task Sync_and_async_refinement_conversion_agree(
        string annotation,
        object? value,
        bool expectedSuccess)
    {
        // Refinement clauses are the reason the two paths exist at all:
        // a `where` predicate and a `coerce` expression are user code.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            type Port = int where (_ >= 1 and _ <= 65535) coerce 80
            type Positive = int where _ > 0
            """);

        var syncSucceeded = engine.TryConvertAnnotatedValue(annotation, value, out var syncConverted);
        var asyncResult = await engine.TryConvertAnnotatedValueAsync(annotation, value, CancellationToken.None);

        Assert.Equal(expectedSuccess, syncSucceeded);
        Assert.Equal(syncSucceeded, asyncResult.Success);
        Assert.Equal(Describe(syncConverted), Describe(asyncResult.Converted));
    }

    [Fact]
    public async Task Nested_refinements_agree_across_both_paths()
    {
        // A refinement over a refinement exercises the recursive branch,
        // where each level's predicate must run innermost-first.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            type Small = int where (_ < 100)
            type SmallEven = Small where (_ % 2 == 0)
            """);

        foreach (var value in new object?[] { 4, 5, 400, "4" })
        {
            var syncSucceeded = engine.TryConvertAnnotatedValue("SmallEven", value, out var syncConverted);
            var asyncResult = await engine.TryConvertAnnotatedValueAsync("SmallEven", value, CancellationToken.None);

            Assert.Equal(syncSucceeded, asyncResult.Success);
            Assert.Equal(Describe(syncConverted), Describe(asyncResult.Converted));
        }
    }

    [Fact]
    public async Task Post_coercion_predicate_failure_blames_the_same_span_on_both_paths()
    {
        // The tests above compare converted *values*; this one compares the
        // *diagnostic*, which is the axis on which the two implementations of
        // this cluster were found to differ. The predicate succeeds on the
        // original value (returning false) and then throws a non-diagnostic CLR
        // exception on the coerced one: "no".Substring(0, 2) is "no", but
        // "x".Substring(0, 2) is out of range.
        //
        // Scope note, established by running this test against the
        // pre-convergence engine, where it also passed: the sync/async copies
        // did attribute a post-coercion predicate failure to different spans —
        // the coercer's and the predicate's respectively — but the sync copy
        // that carried the odd one out sat behind a re-run that only happens
        // after the sequence has already completed without throwing. A
        // deterministic predicate cannot throw on the second pass having not
        // thrown on the first, so the difference was unreachable without a
        // side-effecting predicate. It was latent, not live. This test therefore
        // guards diagnostic agreement going forward rather than reproducing a
        // historical failure.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            type Fussy = string where (_.Substring(0, 2) == "ok") coerce "x"
            """);

        var syncSignature = DescribeFailure(
            () =>
            {
                engine.TryConvertAnnotatedValue("Fussy", "no", out var converted);
                return converted;
            });

        var asyncSignature = DescribeFailure(
            () => engine
                .TryConvertAnnotatedValueAsync("Fussy", "no", CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult()
                .Converted);

        Assert.Equal(syncSignature, asyncSignature);

        // Pin the direction as well as the agreement: a change that made both
        // paths blame the coercer would keep them equal and still be wrong.
        Assert.Contains("Substring", syncSignature, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reduces a refinement failure to <c>code@start+length</c> plus the source
    /// text the span covers, whether it arrived as a thrown exception or inside a
    /// failure wrapper. Reflection is used because the wrappers are private
    /// records; the alternative is widening their visibility purely for a test.
    /// </summary>
    private static string DescribeFailure(Func<object?> convert)
    {
        ToshDiagnosticException? exception = null;
        object? converted = null;

        try
        {
            converted = convert();
        }
        catch (ToshDiagnosticException thrown)
        {
            exception = thrown;
        }

        exception ??= converted?
            .GetType()
            .GetProperties()
            .Select(property => property.GetValue(converted))
            .OfType<ToshDiagnosticException>()
            .FirstOrDefault();

        if (exception is null)
        {
            return $"no-diagnostic:{Describe(converted)}";
        }

        var diagnostic = exception.Diagnostics[0];
        if (diagnostic.Span is not { } span)
        {
            return $"{diagnostic.Code}@<no-span>";
        }

        var sourceText = diagnostic.SourceText ?? string.Empty;
        var snippet = span.Start >= 0 && span.Start + span.Length <= sourceText.Length
            ? sourceText.Substring(span.Start, span.Length)
            : "<out-of-range>";

        return $"{diagnostic.Code}@{span.Start}+{span.Length}:{snippet}";
    }

    /// <summary>
    /// Compares by shape rather than reference so the failure wrappers
    /// (<c>AnnotationRefinementError</c> / <c>AnnotationRefinementFailure</c>)
    /// are treated as equal when they represent the same outcome.
    /// </summary>
    private static string Describe(object? value) => value switch
    {
        null => "<null>",
        string s => $"string:{s}",
        object[] array => $"array[{array.Length}]",
        _ when value.GetType().Name.Contains("Refinement", StringComparison.Ordinal)
            => $"refinement-failure:{value.GetType().Name}",
        _ => $"{value.GetType().Name}:{value}",
    };
}

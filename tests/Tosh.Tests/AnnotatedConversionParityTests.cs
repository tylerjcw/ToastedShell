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

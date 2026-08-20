using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests for the lowering-time pipeline fusions and their evaluator
/// dispatch path. The contract: a fused pipeline yields the same
/// observable results as the unfused one, with smaller allocations.
/// </summary>
public sealed class PipelineFusionTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public PipelineFusionTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    [Fact]
    public void Lowering_attaches_sort_first_fusion()
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse("1..100 | where $_ > 50 | sort | first 5", "<fuse>");
        Lowerer.Lower(parse, _runtime.Commands);

        var pipeline = ((PipelineStatementSyntax)parse.Statement).Pipeline;
        Assert.IsType<SortFirstFusion>(pipeline.Fusion);

        var fusion = (SortFirstFusion)pipeline.Fusion!;
        Assert.Equal(5, fusion.Count);
        Assert.False(fusion.Reverse);
        Assert.Equal(2, fusion.StagesConsumed);
    }

    [Fact]
    public void Lowering_recognises_reverse_flag()
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse("1..100 | sort -r | first 3", "<fuse>");
        Lowerer.Lower(parse, _runtime.Commands);

        var pipeline = ((PipelineStatementSyntax)parse.Statement).Pipeline;
        var fusion = pipeline.Fusion as SortFirstFusion;
        Assert.NotNull(fusion);
        Assert.True(fusion!.Reverse);
        Assert.Equal(3, fusion.Count);
    }

    [Fact]
    public void Lowering_refuses_when_sort_has_key_selector()
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse("ls | sort Name | first 5", "<fuse>");
        Lowerer.Lower(parse, _runtime.Commands);

        var pipeline = ((PipelineStatementSyntax)parse.Statement).Pipeline;
        Assert.Null(pipeline.Fusion);
    }

    [Fact]
    public void Lowering_refuses_when_sort_has_unique_or_numeric_flag()
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse("1..10 | sort -u | first 3", "<fuse>");
        Lowerer.Lower(parse, _runtime.Commands);

        var pipeline = ((PipelineStatementSyntax)parse.Statement).Pipeline;
        Assert.Null(pipeline.Fusion);
    }

    [Fact]
    public void Lowering_refuses_when_first_count_is_dynamic()
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse("var n = 5\n1..10 | sort | first $n", "<fuse>");
        Lowerer.Lower(parse, _runtime.Commands);

        // Two statements; pipeline is in the second.
        var stmts = ((ScriptStatementSyntax)parse.Statement).Statements;
        var pipeline = ((PipelineStatementSyntax)stmts[1]).Pipeline;
        Assert.Null(pipeline.Fusion);
    }

    [Fact]
    public async Task Fused_path_matches_unfused_path_default()
    {
        var engine = new ToshEngine(_runtime);
        var fused = await engine.ExecuteToListAsync("1..100 | where $_ > 50 | sort | first 5");

        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var unfused = await new ToshEngine(_runtime).ExecuteToListAsync("1..100 | where $_ > 50 | sort | first 5");
            Assert.Equal(unfused, fused);
            Assert.Equal(new object?[] { 51, 52, 53, 54, 55 }, fused);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }

    [Fact]
    public async Task Fused_path_matches_unfused_path_reverse()
    {
        var engine = new ToshEngine(_runtime);
        var fused = await engine.ExecuteToListAsync("1..100 | sort -r | first 3");

        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var unfused = await new ToshEngine(_runtime).ExecuteToListAsync("1..100 | sort -r | first 3");
            Assert.Equal(unfused, fused);
            Assert.Equal(new object?[] { 100, 99, 98 }, fused);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }

    [Fact]
    public async Task Fused_path_handles_first_with_no_count()
    {
        var engine = new ToshEngine(_runtime);
        var result = await engine.ExecuteToListAsync("1..10 | sort | first");
        Assert.Equal(new object?[] { 1 }, result);
    }

    [Fact]
    public async Task Fused_path_handles_strings()
    {
        var engine = new ToshEngine(_runtime);
        // `each` flattens the list so each string flows through the pipeline
        // individually. This once *had* to be written with the `each`: without it the
        // fusion received the array as one item and returned it unsorted (`TOAST-0025`).
        // Kept in that form as a control — the `each` spelling must keep working — with
        // the bare-literal spelling pinned below.
        var result = await engine.ExecuteToListAsync(
            "[\"banana\", \"apple\", \"cherry\"] | each { $_ } | sort | first 2");
        Assert.Equal(2, result.Count);
        Assert.Equal("apple", result[0]);
        Assert.Equal("banana", result[1]);
    }

    /// <summary>
    /// A bare collection literal is a source shape this corpus did not have — and it was
    /// the one that was broken. `TOAST-0025`.
    /// </summary>
    /// <remarks>
    /// Every case here answered `3, 1, 2` before the fix: the whole array, unsorted, with
    /// `first` not applied either. The fusion consumed whatever the head yielded, and
    /// `TS-P2-74` makes a head yield a lone collection as **one value** deliberately —
    /// "it is each stage that decides whether a collection means itself or its elements".
    /// The fusion stood in for two stages that had both decided, and decided neither.
    ///
    /// Every existing test in this file used `1..100`, `1..10`, or a literal pushed
    /// through `each`, so all three avoided it.
    /// </remarks>
    [Theory]
    [InlineData("[3,1,2] | sort | first", new object?[] { 1 })]
    [InlineData("[3,1,2] | sort | first 2", new object?[] { 1, 2 })]
    [InlineData("[3,1,2] | sort -r | first", new object?[] { 3 })]
    [InlineData("[3,1,2] | sort -r | first 2", new object?[] { 3, 2 })]
    [InlineData("[\"c\",\"a\",\"b\"] | sort | first 2", new object?[] { "a", "b" })]
    public async Task Fused_path_expands_a_collection_literal_source(string source, object?[] expected)
    {
        var engine = new ToshEngine(_runtime);
        Assert.Equal(expected, await engine.ExecuteToListAsync(source));
    }

    [Fact]
    public async Task Fused_path_matches_unfused_path_for_a_collection_literal()
    {
        const string Source = "[3,1,2] | sort | first 2";

        var engine = new ToshEngine(_runtime);
        var fused = await engine.ExecuteToListAsync(Source);

        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var unfused = await new ToshEngine(_runtime).ExecuteToListAsync(Source);
            Assert.Equal(unfused, fused);
            Assert.Equal(new object?[] { 1, 2 }, fused);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }

    /// <summary>
    /// The expansion is the one the pipeline already defines, not a looser one.
    /// </summary>
    /// <remarks>
    /// A string is a collection of characters to the CLR and an atom to the shell, so the
    /// naive repair — expanding every item — turns `"hello" | sort | first` into `"e"`.
    /// `ReplaySingleInputCollectionAsync` is used precisely because it already draws this
    /// line, and `sort` alone draws it the same way.
    /// </remarks>
    [Fact]
    public async Task A_string_source_is_not_expanded_into_characters()
    {
        var engine = new ToshEngine(_runtime);
        Assert.Equal(new object?[] { "hello" }, await engine.ExecuteToListAsync("\"hello\" | sort | first"));
    }

    /// <summary>
    /// Only a *lone* collection expands, and only one level.
    /// </summary>
    /// <remarks>
    /// The two halves of the trap the naive repair falls into. A replayed variable is a
    /// `PreExpandedSequence` (`TS-P2-113`) whose items are already elements, so expanding
    /// again would turn a stream of arrays into a stream of numbers; and a lone array of
    /// arrays must yield arrays, not their contents. Both answer `Int32[]`, and both
    /// answered `Int32[]` before the fix too — which is why they are controls: the repair
    /// had to leave them alone.
    /// </remarks>
    [Theory]
    [InlineData("var v = [[3,4],[1,2]]\n($v | sort | first).GetType().Name")]
    [InlineData("([[3,4],[1,2]] | sort | first).GetType().Name")]
    public async Task A_collection_of_collections_expands_exactly_one_level(string source)
    {
        var engine = new ToshEngine(_runtime);
        var results = await engine.ExecuteToListAsync(source);
        Assert.Equal("Int32[]", results[^1]);
    }
}

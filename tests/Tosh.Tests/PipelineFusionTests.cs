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
        // `each` flattens the list so each string flows through the
        // pipeline individually. Without the each, the array is a
        // single pipeline value.
        var result = await engine.ExecuteToListAsync(
            "[\"banana\", \"apple\", \"cherry\"] | each { $_ } | sort | first 2");
        Assert.Equal(2, result.Count);
        Assert.Equal("apple", result[0]);
        Assert.Equal("banana", result[1]);
    }
}

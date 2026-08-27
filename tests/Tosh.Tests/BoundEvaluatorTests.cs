using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests for the bound-IR evaluator facade. v1 just delegates to the
/// parse-tree evaluator, so the contract is "produces the same values
/// as <see cref="ToshEngine.ExecuteToListAsync(string, CancellationToken)"/>".
/// As individual carved-out shapes get fast paths, the same tests
/// will still pass — they describe behavior, not the path taken.
/// </summary>
public sealed class BoundEvaluatorTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public BoundEvaluatorTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    [Fact]
    public async Task BoundEvaluator_runs_a_simple_echo_and_returns_its_value()
    {
        var engine = new ToshEngine(_runtime.Language);
        var results = await BoundEvaluator.EvaluateToListAsync(engine, "echo hello");

        Assert.Single(results);
        Assert.Equal("hello", results[0]);
    }

    [Fact]
    public async Task BoundEvaluator_matches_parse_tree_evaluator_for_var_then_echo()
    {
        const string source = "var x = 42\necho $x";

        var engineA = new ToshEngine(_runtime.Language);
        var engineB = new ToshEngine(_runtime.Language);

        var fromBound = await BoundEvaluator.EvaluateToListAsync(engineA, source);
        var fromParse = await engineB.ExecuteToListAsync(source);

        Assert.Equal(fromParse, fromBound);
    }

    [Fact]
    public async Task BoundEvaluator_matches_parse_tree_evaluator_for_range_pipeline()
    {
        const string source = "1..5 | sum";

        var engineA = new ToshEngine(_runtime.Language);
        var engineB = new ToshEngine(_runtime.Language);

        var fromBound = await BoundEvaluator.EvaluateToListAsync(engineA, source);
        var fromParse = await engineB.ExecuteToListAsync(source);

        Assert.Equal(fromParse, fromBound);
    }

    [Fact]
    public async Task BoundEvaluator_evaluates_explicitly_pre_lowered_unit()
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse("echo $env.HOME", "<bound-test>");
        var unit = Lowerer.Lower(parse, _runtime.Commands);

        var results = new List<object?>();
        await foreach (var v in BoundEvaluator.EvaluateAsync(engine, unit))
        {
            results.Add(v);
        }

        Assert.Single(results);
        Assert.Equal(Environment.GetEnvironmentVariable("HOME"), results[0]);
    }
}

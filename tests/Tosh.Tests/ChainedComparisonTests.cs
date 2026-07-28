using System.Reflection;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-22 — `a &lt; b &lt; c` is a real chained comparison meaning
/// `(a &lt; b) and (b &lt; c)`. Each operand is evaluated at most once
/// and evaluation short-circuits, so a chain is not equivalent to
/// rewriting the source with the middle operand repeated.
/// </summary>
[Collection(ConsoleSerialCollection.Name)]
public sealed class ChainedComparisonTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public ChainedComparisonTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    [Theory]
    [InlineData("1 < 2 < 3", true)]
    [InlineData("3 < 2 < 1", false)]
    [InlineData("1 < 2 < 1", false)]
    [InlineData("1 <= 1 <= 1", true)]
    [InlineData("1 < 2 > 0", true)]
    [InlineData("1 == 1 == 1", true)]
    [InlineData("1 < 2 < 3 < 4", true)]
    [InlineData("1 < 2 < 9 < 4", false)]
    [InlineData("1 < 2", true)]
    public async Task Chains_evaluate_as_a_conjunction_of_adjacent_pairs(string expression, bool expected)
    {
        var engine = new ToshEngine(_runtime);
        await engine.ExecuteToListAsync($"var result = ({expression})");

        Assert.True(engine.TryGetVariableValue("result", out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task An_interior_operand_is_evaluated_exactly_once()
    {
        // The reason a chain is its own node: desugaring to
        // `(1 < mid()) and (mid() < 3)` would call mid twice.
        var engine = new ToshEngine(_runtime);
        await engine.ExecuteToListAsync(
            """
            var calls = 0
            func mid() { $calls = $calls + 1; return 2 }
            var result = (1 < (mid) < 3)
            """);

        Assert.True(engine.TryGetVariableValue("result", out var result));
        Assert.Equal(true, result);
        Assert.True(engine.TryGetVariableValue("calls", out var calls));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_failing_pair_short_circuits_the_rest_of_the_chain()
    {
        var engine = new ToshEngine(_runtime);
        await engine.ExecuteToListAsync(
            """
            var calls = 0
            func third() { $calls = $calls + 1; return 9 }
            var result = (5 < 2 < (third))
            """);

        Assert.True(engine.TryGetVariableValue("result", out var result));
        Assert.Equal(false, result);
        Assert.True(engine.TryGetVariableValue("calls", out var calls));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Non_relational_operators_do_not_chain()
    {
        // `is` has no useful chained reading, so it keeps its
        // left-associative behaviour.
        var engine = new ToshEngine(_runtime);
        await engine.ExecuteToListAsync("var result = (1 is int)");

        Assert.True(engine.TryGetVariableValue("result", out var result));
        Assert.Equal(true, result);
    }

    [Theory]
    [InlineData("1 < 2 < 3", "true")]
    [InlineData("3 < 2 < 1", "false")]
    [InlineData("1 < 2 < 1", "false")]
    [InlineData("1 < 2 < 3 < 4", "true")]
    public void Compiled_chains_match_the_interpreter(string expression, string expected)
    {
        Assert.Equal(expected, CompileAndRun($"echo ({expression})"));
    }

    [Fact]
    public void Compiled_chains_evaluate_each_operand_once_and_short_circuit()
    {
        var evaluatedOnce = CompileAndRun(
            """
            var n: int = 0
            func mid() -> int { $n = $n + 1; return 2 }
            var r = (1 < (mid) < 3)
            echo $n
            """);
        Assert.Equal("1", evaluatedOnce);

        var shortCircuited = CompileAndRun(
            """
            var n: int = 0
            func third() -> int { $n = $n + 1; return 9 }
            var r = (5 < 2 < (third))
            echo $n
            """);
        Assert.Equal("0", shortCircuited);
    }

    private string CompileAndRun(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<chained-comparison-test>");
        Assert.True(
            parse.Diagnostics.Count == 0,
            $"parse errors: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);
        var assemblyName = $"ToshChainedComparison_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var emit = BoundUnitEmitter.Emit(unit, assemblyName, stream);
        Assert.True(
            emit.IsClean,
            $"unexpected diagnostics: {string.Join(", ", emit.UnsupportedShapes)}");

        var assembly = Assembly.Load(stream.ToArray());
        var main = assembly
            .GetType($"{assemblyName}.Program")!
            .GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            main.Invoke(null, new object?[] { Array.Empty<string>() });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return capture.ToString().ReplaceLineEndings("\n").TrimEnd('\n');
    }
}

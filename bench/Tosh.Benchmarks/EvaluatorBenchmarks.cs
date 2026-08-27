using BenchmarkDotNet.Attributes;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Benchmarks;

/// <summary>
/// End-to-end pipeline benchmarks: parse + bind + evaluate via
/// <see cref="ToshEngine.ExecuteToListAsync(string, CancellationToken)"/>.
///
/// Inputs are intentionally pure (no I/O, no external processes) so the
/// timings reflect the engine itself rather than the operating system.
/// </summary>
[MemoryDiagnoser]
public class EvaluatorBenchmarks
{
    private ToshEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        _engine = new ToshEngine(runtime.Language);
    }

    /// <summary>Trivial expression — single literal pipeline.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> Echo()
    {
        var results = await _engine.ExecuteToListAsync("echo hello");
        return results.Count;
    }

    /// <summary>Variable declaration + reference.</summary>
    [Benchmark]
    public async Task<int> VariableDeclaration()
    {
        var results = await _engine.ExecuteToListAsync("var x = 42\necho $x");
        return results.Count;
    }

    /// <summary>Arithmetic over a 1..10 range via 'sum'.</summary>
    [Benchmark]
    public async Task<int> ListSum()
    {
        var results = await _engine.ExecuteToListAsync("1..10 | sum");
        return results.Count;
    }

    /// <summary>Range-only iteration (no downstream consumer).</summary>
    [Benchmark]
    public async Task<int> RangeMaterialize100()
    {
        var results = await _engine.ExecuteToListAsync("1..100 | count");
        return results.Count;
    }

    /// <summary>Range + identity each (probes per-item dispatch overhead).</summary>
    [Benchmark]
    public async Task<int> RangeEachIdentity100()
    {
        var results = await _engine.ExecuteToListAsync("1..100 | each { $_ } | count");
        return results.Count;
    }

    /// <summary>'where' filter + 'sort' on a 100-element range.</summary>
    [Benchmark]
    public async Task<int> WhereSort()
    {
        var results = await _engine.ExecuteToListAsync(
            "1..100 | where $_ > 50 | sort | first 5");
        return results.Count;
    }

    /// <summary>
    /// Same input as <see cref="WhereSort"/> but with the lowering
    /// pass disabled — head-to-head measurement of the sort+first
    /// fusion's payoff.
    /// </summary>
    [Benchmark]
    public async Task<int> WhereSortNoFuse()
    {
        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var results = await _engine.ExecuteToListAsync(
                "1..100 | where $_ > 50 | sort | first 5");
            return results.Count;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }

    /// <summary>Function call dispatch overhead.</summary>
    [Benchmark]
    public async Task<int> FunctionCall()
    {
        var results = await _engine.ExecuteToListAsync(
            "func square(n) { $n * $n }\nsquare 7");
        return results.Count;
    }

    /// <summary>Nested for-loop body.</summary>
    [Benchmark]
    public async Task<int> ForLoop()
    {
        var results = await _engine.ExecuteToListAsync(
            "for i in [1, 2, 3, 4, 5] { echo $i }");
        return results.Count;
    }

    /// <summary>Interpolated string formatting.</summary>
    [Benchmark]
    public async Task<int> InterpolatedString()
    {
        var results = await _engine.ExecuteToListAsync(
            "var name = \"world\"\necho $\"hello, {$name}!\"");
        return results.Count;
    }

    /// <summary>
    /// Arithmetic-heavy expression with no runtime variables. With
    /// constant folding this should shrink to a single-literal echo;
    /// without it, the evaluator walks every node.
    /// </summary>
    [Benchmark]
    public async Task<int> ArithmeticConstants()
    {
        var results = await _engine.ExecuteToListAsync(
            "echo (60 * 60 * 24 + 12 * 60 - 7)");
        return results.Count;
    }

    /// <summary>
    /// Same arithmetic expression with the lowering pass disabled —
    /// gives us a head-to-head measurement of the fold's payoff.
    /// </summary>
    [Benchmark]
    public async Task<int> ArithmeticConstantsNoFold()
    {
        Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", "1");
        try
        {
            var results = await _engine.ExecuteToListAsync(
                "echo (60 * 60 * 24 + 12 * 60 - 7)");
            return results.Count;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOSH_DISABLE_LOWERER", null);
        }
    }
}

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
        _engine = new ToshEngine(runtime);
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

    /// <summary>'where' filter + 'sort' on a 100-element range.</summary>
    [Benchmark]
    public async Task<int> WhereSort()
    {
        var results = await _engine.ExecuteToListAsync(
            "1..100 | where $_ > 50 | sort | first 5");
        return results.Count;
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
}

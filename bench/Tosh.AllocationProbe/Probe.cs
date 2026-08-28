using System.Diagnostics;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.AllocationProbe;

/// <summary>
/// Measures bytes and nanoseconds per loop iteration for one expression shape.
/// </summary>
/// <remarks>
/// <para>
/// The method is <c>TS-P2-125</c>'s: run a loop of a known length, take
/// <see cref="GC.GetTotalAllocatedBytes(bool)"/> either side, and divide. An empty loop body
/// gives the baseline every other shape is read against, because most of the cost of a small
/// expression is the iteration around it, not the expression.
/// </para>
/// <para>
/// Allocation is deterministic and repeats exactly; time does not, so the best of several runs
/// is taken. That item records two occasions where a cross-run timing comparison nearly caused
/// a wrong conclusion — treat the byte column as the measurement and the nanosecond column as
/// an indication.
/// </para>
/// </remarks>
public static class Probe
{
    /// <param name="shape">The loop body to measure.</param>
    /// <param name="iterations">Loop length. Larger amortises the fixed cost of the run.</param>
    /// <param name="runs">How many times to measure; the smallest result wins.</param>
    public static async Task<Measurement> MeasureAsync(Shape shape, int iterations, int runs)
    {
        var source = $"{Shapes.Preamble}\nfor i in 1..{iterations} {{ {shape.Body} }}\n";

        // Warm up: JIT, and every allocation that happens once rather than per iteration.
        await RunAsync(source);

        var bytes = double.MaxValue;
        var nanos = double.MaxValue;

        for (var run = 0; run < runs; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            await RunAsync(source);
            watch.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);

            bytes = Math.Min(bytes, (after - before) / (double)iterations);
            nanos = Math.Min(nanos, watch.Elapsed.TotalNanoseconds / iterations);
        }

        return new Measurement(shape.Name, bytes, nanos);
    }

    /// <summary>
    /// A fresh engine per run, with output discarded — a shared one would carry state from
    /// the previous shape into the next one's numbers.
    /// </summary>
    private static async Task RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Output = TextWriter.Null;
        runtime.Error = TextWriter.Null;
        var engine = new ToshEngine(runtime.Language);
        await engine.ExecuteToListAsync(source);
    }
}

/// <param name="Name">The shape's name.</param>
/// <param name="BytesPerIteration">Allocated bytes per loop iteration.</param>
/// <param name="NanosecondsPerIteration">Elapsed time per loop iteration.</param>
public readonly record struct Measurement(
    string Name,
    double BytesPerIteration,
    double NanosecondsPerIteration);

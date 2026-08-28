using Tosh.AllocationProbe;

// Per-iteration allocation for the interpreter's expression shapes.
//
//   dotnet run -c Release --project bench/Tosh.AllocationProbe
//   dotnet run -c Release --project bench/Tosh.AllocationProbe -- --iterations 500000
//   dotnet run -c Release --project bench/Tosh.AllocationProbe -- --markdown
//
// This is not BenchmarkDotNet and does not replace it. It answers one question in seconds
// rather than minutes: how many bytes does one more iteration of this loop cost? That is the
// question `TS-P2-125` kept re-deriving by hand, which is why it lives here now.

var iterations = 200_000;
var runs = 3;
var markdown = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--iterations" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n):
            iterations = n;
            i++;
            break;

        case "--runs" when i + 1 < args.Length && int.TryParse(args[i + 1], out var r):
            runs = r;
            i++;
            break;

        case "--markdown":
            markdown = true;
            break;

        case "--help" or "-h":
            Console.WriteLine("usage: [--iterations N] [--runs N] [--markdown]");
            return 0;

        default:
            Console.Error.WriteLine($"unknown argument '{args[i]}' — try --help");
            return 1;
    }
}

// One pass over every shape before measuring any, so the first row does not absorb the JIT
// cost of the whole evaluator and report it as that shape's allocation.
foreach (var shape in Shapes.Default)
{
    await Probe.MeasureAsync(shape, iterations, runs: 1);
}

var results = new List<Measurement>();

foreach (var shape in Shapes.Default)
{
    results.Add(await Probe.MeasureAsync(shape, iterations, runs));
}

var baseline = results[0].BytesPerIteration;

if (markdown)
{
    Console.WriteLine("| shape | bytes/iter | ns/iter | over empty |");
    Console.WriteLine("|---|---:|---:|---:|");

    foreach (var result in results)
    {
        var over = result.Name == "empty" ? "—" : $"{result.BytesPerIteration - baseline:N0}";
        Console.WriteLine(
            $"| `{result.Name}` | {result.BytesPerIteration:N0} | {result.NanosecondsPerIteration:N0} | {over} |");
    }
}
else
{
    Console.WriteLine($"{iterations:N0} iterations, best of {runs}\n");
    Console.WriteLine($"{"shape",-18} {"bytes/iter",12} {"ns/iter",10} {"over empty",12}");
    Console.WriteLine(new string('-', 56));

    foreach (var result in results)
    {
        var over = result.Name == "empty" ? "" : $"{result.BytesPerIteration - baseline,12:N0}";
        Console.WriteLine(
            $"{result.Name,-18} {result.BytesPerIteration,12:N0} {result.NanosecondsPerIteration,10:N0} {over}");
    }
}

return 0;

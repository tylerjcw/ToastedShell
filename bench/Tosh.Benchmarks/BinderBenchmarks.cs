using BenchmarkDotNet.Attributes;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Benchmarks;

/// <summary>
/// Measures the cost of <see cref="Binder.Bind"/> on representative inputs.
/// Parsing is done once in <see cref="GlobalSetup"/> so each benchmark
/// timing only covers the bind pass itself.
///
/// Inputs:
///   tiny     — single command (typical REPL line).
///   small    — short pipeline of ~5 commands.
///   medium   — ~50-line script with control flow and pipes.
///   large    — ~500-line script (synthetic; stress test).
/// </summary>
[MemoryDiagnoser]
public class BinderBenchmarks
{
    private ToshRuntime _runtime = null!;
    private ParseResult _tiny = null!;
    private ParseResult _small = null!;
    private ParseResult _medium = null!;
    private ParseResult _large = null!;

    [GlobalSetup]
    public void Setup()
    {
        _runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(_runtime);

        _tiny = engine.Parse("ls -la", "<bench-tiny>");

        _small = engine.Parse(
            "ls -la | where _.Size > 1024 | sort-by Size | head 10",
            "<bench-small>");

        _medium = engine.Parse(BuildMediumScript(), "<bench-medium>");
        _large = engine.Parse(BuildLargeScript(), "<bench-large>");
    }

    [Benchmark(Baseline = true)]
    public int Tiny() => Binder.Bind(_tiny, _runtime.Commands).Count;

    [Benchmark]
    public int Small() => Binder.Bind(_small, _runtime.Commands).Count;

    [Benchmark]
    public int Medium() => Binder.Bind(_medium, _runtime.Commands).Count;

    [Benchmark]
    public int Large() => Binder.Bind(_large, _runtime.Commands).Count;

    [Benchmark]
    public int LargeNonInteractive() => Binder.Bind(_large, _runtime.Commands, isInteractive: false).Count;

    private static string BuildMediumScript()
    {
        // Realistic-ish profile fragment: declarations, exports, a function,
        // a for-loop, a try/catch, a small pipeline.
        return """
        var greeting = "hello"
        var count = 5
        export EDITOR = "nvim"
        export PATH = "$env.HOME/.local/bin:$env.PATH"

        func ll => ls -la
        func gs => git status
        func mkcd(dir) {
            mkdir $dir
            cd $dir
        }

        for $i in [1, 2, 3, 4, 5] {
            echo $i
            ls -la | where _.Type == file | sort-by Size | head 3
        }

        try {
            cat /etc/hostname | head 1
        } catch $err {
            echo $"Error: ${err}"
        }

        if $count > 3 {
            echo "big"
        } else {
            echo "small"
        }

        ls | where _.Size > 1024 | sort-by Size | reverse | head 10
        ps | where _.Cpu > 0.1 | sort-by Cpu | head 5
        env | where _.Name == "PATH" | first
        """;
    }

    private static string BuildLargeScript()
    {
        // 500 simple statements — purely a stress test.
        var sb = new System.Text.StringBuilder(capacity: 32 * 1024);
        for (int i = 0; i < 500; i++)
        {
            sb.Append("ls -la | where _.Size > ")
              .Append(i)
              .AppendLine(" | sort-by Size | head 10");
        }
        return sb.ToString();
    }
}

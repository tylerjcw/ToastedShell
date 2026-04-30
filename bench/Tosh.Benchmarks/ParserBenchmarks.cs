using BenchmarkDotNet.Attributes;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Benchmarks;

/// <summary>
/// Measures the cost of <see cref="ToshEngine.Parse(string, string)"/>
/// (lex + parse, no binding, no evaluation) on representative inputs.
/// </summary>
[MemoryDiagnoser]
public class ParserBenchmarks
{
    private ToshEngine _engine = null!;
    private string _tiny = null!;
    private string _small = null!;
    private string _medium = null!;
    private string _large = null!;

    [GlobalSetup]
    public void Setup()
    {
        var runtime = ToshRuntime.CreateDefault();
        _engine = new ToshEngine(runtime);

        _tiny = "ls -la";
        _small = "ls -la | where _.Size > 1024 | sort-by Size | head 10";
        _medium = BuildMediumScript();
        _large = BuildLargeScript();
    }

    [Benchmark(Baseline = true)]
    public int Tiny() => _engine.Parse(_tiny, "<bench>").Statement is null ? 0 : 1;

    [Benchmark]
    public int Small() => _engine.Parse(_small, "<bench>").Statement is null ? 0 : 1;

    [Benchmark]
    public int Medium() => _engine.Parse(_medium, "<bench>").Statement is null ? 0 : 1;

    [Benchmark]
    public int Large() => _engine.Parse(_large, "<bench>").Statement is null ? 0 : 1;

    private static string BuildMediumScript()
    {
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

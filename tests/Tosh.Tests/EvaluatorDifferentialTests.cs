using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The synchronous fast path answers exactly what the full evaluator answers — <c>TOAST-0009</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>EvaluateArgumentAsync</c> dispatches to a small synchronous switch for shapes with no
/// suspension point, and falls through to a thirty-nine-case async switch otherwise. Every shape
/// the fast path answers is therefore a <em>second copy</em> of semantics defined elsewhere, and
/// the risk is a copy that is nearly the same: the comment on it says so, and relies on each case
/// declining rather than guessing.
/// </para>
/// <para>
/// That was a discipline with nothing checking it. This checks it, by evaluating the same source
/// twice — once normally, once with the fast path suppressed — and requiring the two to agree.
/// </para>
/// <para>
/// It is also the harness the bound-tree evaluator will be built against. A rewrite of
/// semantics-carrying code needs a differential net before the first node moves, and this is that
/// net: the seam it switches on is where a second evaluator will hang.
/// </para>
/// </remarks>
public sealed class EvaluatorDifferentialTests
{
    private static async Task<string> RunAsync(string source, bool suppressFastPath)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language)
        {
            SuppressSimpleArgumentFastPath = suppressFastPath,
        };

        var results = await engine.ExecuteToListAsync(source);
        return string.Join("", results.Select(value => value?.ToString() ?? "null"));
    }

    /// <summary>
    /// Shapes covering every case the fast path claims, and the boundaries where it declines.
    /// </summary>
    public static TheoryData<string> Shapes() =>
    [
        "echo 1",
        "echo \"text\"",
        "echo true",
        "echo null",
        "var t = 7\necho $t",
        "var t = 7\necho ($t)",
        "var t = 7\necho ((($t)))",
        "var t = 7\necho ($t + 1)",
        "var t = 7\necho ($t + 1 + 2 + 3)",
        "var t = 7\necho ($t * 2.5)",
        "var t = 7\necho ($t - 10)",
        "var t = 7\necho ($t / 2)",
        "var t = 7\necho ($t % 3)",
        "echo (2 ** 10)",
        "var t = 7\necho (-$t)",
        "var t = 7\necho ($t + 0.5)",
        "echo (1 + 2.0)",
        "echo (0.1 + 0.2)",
        "echo (2147483647 + 1)",
        "var t = 7\necho ($t == 7)",
        "var t = 7\necho ($t < 5)",
        "echo (\"a\" + \"b\")",
        "var a = [1, 2, 3]\necho $a[0]",
        "var r = \"abc\"\necho $r.Length",
        "var t = 7\necho ($t is int)",
        "echo (1 / 0)",
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_fast_path_and_the_full_evaluator_agree(string source)
    {
        var fast = await OutcomeAsync(source, suppressFastPath: false);
        var full = await OutcomeAsync(source, suppressFastPath: true);

        Assert.Equal(full, fast);
    }

    /// <summary>An answer, or the failure that replaced it — both have to match.</summary>
    private static async Task<string> OutcomeAsync(string source, bool suppressFastPath)
    {
        try
        {
            return await RunAsync(source, suppressFastPath);
        }
        catch (Exception error)
        {
            return "!" + error.GetType().Name;
        }
    }

    [Fact]
    public async Task The_seam_is_observable_and_harmless()
    {
        // A differential harness whose two sides run the same code proves nothing. The seam has
        // to exist and to leave answers alone; if it were ignored, every case above would be
        // comparing one path against itself.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        Assert.False(engine.SuppressSimpleArgumentFastPath);
        engine.SuppressSimpleArgumentFastPath = true;
        Assert.True(engine.SuppressSimpleArgumentFastPath);

        var results = await engine.ExecuteToListAsync("echo (7 + 1)");
        Assert.Equal("8", string.Join("", results.Select(v => v?.ToString())));
    }
}

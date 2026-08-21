using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `...` spreads into a pipeline — `TOAST-0032`.
/// </summary>
/// <remarks>
/// <para>
/// The operator already worked in three places — an array literal, a record literal and an
/// argument list — and stopped at the one a shell needs most. `...$xs | count` reached
/// command position and was reported as an unknown command, because `...$xs` lexes as a
/// single bareword.
/// </para>
/// <para>
/// It matters beyond convenience. `TS-P3-04` asked for the cardinality lookahead to be
/// removed "while preserving object-valued pipelines and **a reasonable migration path**",
/// and the migration path was the clause nobody had built: with no way to *ask* for
/// spreading, changing the default is all-or-nothing across the whole standard library.
/// Two attempts at `TOAST-0028` died on exactly that. `| each { $_ }` was the only spelling
/// available, and it allocates a block invocation per item to say "these are separate
/// things".
/// </para>
/// <para>
/// This is the one form where shape is not inferred at all: the author has said what they
/// meant, at the point they meant it.
/// </para>
/// </remarks>
public sealed class PipelineSpreadTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Xs = "var xs = [1, 2, 3]\n";

    /// <summary>A spread at the head sends elements, one item each.</summary>
    [Theory]
    [InlineData("(...$xs | count)", "3")]
    [InlineData("(...$xs | first)", "1")]
    [InlineData("(...$xs | where $_ > 1 | count)", "2")]
    public async Task A_spread_head_sends_elements(string source, string expected)
        => Assert.Equal(expected, await RunAsync(Xs + source));

    /// <summary>
    /// It spreads one level, and only what the language calls a sequence.
    /// </summary>
    /// <remarks>
    /// The same predicate the pipeline uses elsewhere, so `...` cannot come to disagree
    /// with `§Collection Shape` about what a sequence is: a record and a string are single
    /// values, and spreading one yields it unchanged rather than taking it apart.
    /// </remarks>
    [Theory]
    [InlineData("var v = [[1,2],[3]]\n(...$v | count)", "2")]
    [InlineData("var v = {| a = 1, b = 2 |}\n(...$v | count)", "1")]
    [InlineData("var v = {% \"a\" => 1 %}\n(...$v | count)", "1")]
    [InlineData("var v = \"abc\"\n(...$v | count)", "1")]
    public async Task A_spread_expands_one_level_and_respects_atoms(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The three contexts that already worked are untouched, pinned as controls.
    /// </summary>
    /// <remarks>
    /// The change adds a branch to pipeline-stage parsing ahead of command dispatch, so
    /// the risk was that it captured `...` where another context had it. These are the
    /// three it must not have taken.
    /// </remarks>
    [Theory]
    // Array literal.
    [InlineData("var xs = [1,2,3]\nvar a = [0, ...$xs, 4]\n($a | count)", "5")]
    // Record literal.
    [InlineData("var b = {| a = 1 |}\nvar m = {| ...$b, b = 2 |}\n($m.b)", "2")]
    // Function splatting.
    [InlineData("func t3(a, b, c) => $\"{$a}-{$b}-{$c}\"\nvar xs = [1,2,3]\n(t3 ...$xs)", "1-2-3")]
    public async Task The_existing_spread_contexts_are_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// It is the migration path a shape change needs.
    /// </summary>
    /// <remarks>
    /// `TOAST-0028` would make a command or generator's collection a single value. When it
    /// lands, this is the spelling that gets the old behaviour back — so the case is pinned
    /// now, while the default still spreads, and will keep passing after it does not.
    /// </remarks>
    [Fact]
    public async Task It_gives_a_spelling_for_what_the_default_may_stop_doing()
    {
        // Written against a collection held in a variable, which is what `...` acts on, and
        // deliberately *not* against `one | first`: that answers `10` today because the
        // lookahead already spread the generator's array, and will answer the array once
        // `TOAST-0028` lands. Asserting either would pin the behaviour under change rather
        // than this operator.
        const string Held = "var v = [10, 20, 30]\n";

        Assert.Equal("3", await RunAsync(Held + "(...$v | count)"));
        Assert.Equal("10", await RunAsync(Held + "(...$v | first)"));

        // A variable already spreads today, so `$v | first` agrees with `...$v | first`
        // and there is no contrast to draw here — pinning one would only pin the default.
        // What `...` adds is that its answer is not the default's to change.
        Assert.Equal(
            await RunAsync(Held + "($v | first)"),
            await RunAsync(Held + "(...$v | first)"));
    }
}

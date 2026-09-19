using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A script's own error, where a platform call has buried it.
/// </summary>
/// <remarks>
/// <para>
/// A CLR API that calls back into a script catches what the callback raises and reports its
/// own failure instead. <c>List&lt;T&gt;.Sort</c> answers "Failed to compare two elements in
/// the array" and keeps the real cause as an inner exception — so the author was told that
/// sentence, pointed at the line that called <c>Sort</c>, with nothing anywhere to say that
/// their own <c>Compare</c> had thrown.
/// </para>
/// <para>
/// This became reachable when a tōsh class could be handed to the platform as one of its own
/// kind: the language is now on both sides of such a call, so an error can leave a script,
/// cross into .NET, and come back wearing .NET's words.
/// </para>
/// </remarks>
public sealed class ScriptCauseThroughClrTests
{
    private static async Task<ToshDiagnostic> FailureOf(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(source));

        return error.Diagnostics[0];
    }

    /// <summary>A bare <c>throw</c> inside a callback is named as the cause.</summary>
    [Fact]
    public async Task A_thrown_value_survives_the_platforms_wrapper()
    {
        var diagnostic = await FailureOf(
            """
            class Boom extends System.Collections.Generic.Comparer<string> {
                func Compare(a, b) { throw "compare exploded" }
            }
            var s = new System.Collections.Generic.List<string>(["b", "a"])
            $s.Sort(new Boom())
            """);

        // The platform's failure is real and stays the title: it is what a .NET caller would
        // be shown, and the sort genuinely did fail.
        Assert.Contains("Failed to compare", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("compare exploded", diagnostic.Help ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A declared error survives too, not only a thrown string.
    /// </summary>
    /// <remarks>
    /// The two arrive as unrelated CLR types — a bare <c>throw</c> raises a signal, a
    /// declared one raises a <c>ToshError</c> — so matching the signal alone found the first
    /// and missed the second. <c>IToshFailure</c> is the language's own word for either.
    /// </remarks>
    [Fact]
    public async Task A_declared_error_survives_the_platforms_wrapper()
    {
        var diagnostic = await FailureOf(
            """
            class MyErr(msg) extends Error($msg) { }
            class Boom extends System.Collections.Generic.Comparer<string> {
                func Compare(a, b) { throw new MyErr("typed failure") }
            }
            var s = new System.Collections.Generic.List<string>(["b", "a"])
            $s.Sort(new Boom())
            """);

        Assert.Contains("typed failure", diagnostic.Help ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A platform failure with no script behind it gains nothing.
    /// </summary>
    /// <remarks>
    /// The line is worth having only where something was actually buried. Added to every CLR
    /// failure it would be noise, and noise in an error message is worse than silence.
    /// </remarks>
    [Fact]
    public async Task A_platform_failure_of_its_own_gains_no_extra_line()
    {
        var diagnostic = await FailureOf("var n = System.Int32::Parse(\"abc\")");

        Assert.Contains("not in a correct format", diagnostic.Title, StringComparison.Ordinal);
        Assert.Null(diagnostic.Help);
    }

    /// <summary>
    /// An error raised and reported directly is not repeated back to itself.
    /// </summary>
    /// <remarks>
    /// Where the script's error is what failed, the title already says it, and a help line
    /// restating the same sentence is the kind of padding that makes people stop reading
    /// error messages.
    /// </remarks>
    [Fact]
    public async Task An_ordinary_script_error_is_not_restated()
    {
        var diagnostic = await FailureOf("""
            func boom() { throw "plain tosh error" }
            boom
            """);

        Assert.Contains("plain tosh error", diagnostic.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Raised by this script", diagnostic.Help ?? "", StringComparison.Ordinal);
    }
}

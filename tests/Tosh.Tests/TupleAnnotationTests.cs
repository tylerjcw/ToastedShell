using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A tuple type can be written in an annotation — `TOAST-0050`.
/// </summary>
/// <remarks>
/// <para>
/// The type model already had tuples: `TypeNameResolver` resolved `(int, string)` into a
/// `TupleType` with a `DisplayName`, and tuple values and destructuring both worked. The
/// only thing missing was a way to *write* the type, which made it the one part of the type
/// model that could be reached by inference and never by declaration.
/// </para>
/// <para>
/// The gap was in two places, and the second is the one worth remembering. `ParseTypeName`
/// accepted only a bareword — that half is obvious. But `var t: (int, string) = …` did not
/// even reach it: `TryGetTypeNameEndOffset`, a *lookahead* that decides whether `var` starts
/// a declaration at all, also only knew barewords, so the statement fell through to command
/// dispatch and reported "Command 'var' is not a registered builtin" — a message about `var`
/// for a defect in the type annotation. `TS-P2-69` fixed exactly that shape for the `[]`
/// suffix; `TOAST-0002` is about why the two predicates have to agree by hand.
/// </para>
/// </remarks>
public sealed class TupleAnnotationTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private static async Task<string?> DiagnosticCodeAsync(string source)
    {
        try
        {
            await RunAsync(source);
            return null;
        }
        catch (ToshDiagnosticException diagnostic)
        {
            return diagnostic.Diagnostics[0].Code;
        }
    }

    /// <summary>A tuple type is writable in every position an annotation appears.</summary>
    [Theory]
    [InlineData("var t: (int, string) = (1, \"a\")\necho $t.Item1", "1")]
    [InlineData("func two() -> (int, string) {\n return (2, \"b\")\n}\nvar r = two\necho $r.Item2", "b")]
    [InlineData("func takes(p: (int, string)) -> string {\n return $p.Item2\n}\necho (takes (3, \"c\"))", "c")]
    public async Task A_tuple_type_can_be_written_where_a_type_is_expected(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>Nesting, generics and the array suffix work, because elements recurse.</summary>
    [Theory]
    [InlineData("var t: (int, (string, bool)) = (1, (\"x\", true))\necho $t.Item2.Item1", "x")]
    [InlineData("var t: (int, list<string>) = (1, [\"a\"])\necho $t.Item1", "1")]
    [InlineData("var t: (int, string)[] = [(1, \"a\"), (2, \"b\")]\necho $t.Length", "2")]
    public async Task Elements_recurse(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A single parenthesised type is that type, not a one-tuple.
    /// </summary>
    /// <remarks>
    /// The resolver already decided this, and the runtime check has to agree or
    /// `var x: (int)` would bind as one thing and be checked against another.
    /// </remarks>
    [Fact]
    public async Task A_single_parenthesised_type_is_not_a_tuple()
        => Assert.Equal("5", await RunAsync("var x: (int) = 5\necho $x"));

    /// <summary>
    /// The empty tuple type accepts the value the empty tuple literal produces.
    /// </summary>
    /// <remarks>
    /// `()` evaluates to null. Rejecting null for `()` would leave a type that can be
    /// written and never satisfied by the one expression that produces it — the same defect
    /// this item exists to remove, moved one step along.
    /// </remarks>
    [Fact]
    public async Task The_empty_tuple_type_accepts_the_empty_tuple()
        => Assert.Equal("ok", await RunAsync("var e: () = ()\necho \"ok\""));

    /// <summary>Arity is checked.</summary>
    [Theory]
    [InlineData("var t: (int, string) = (1, 2, 3)")]
    [InlineData("var t: (int, string) = (1)")]
    [InlineData("var t: (int, string) = 5")]
    public async Task An_arity_mismatch_is_reported(string source)
        => Assert.Equal("tosh.runtime.annotation_conversion_failed", await DiagnosticCodeAsync(source));

    /// <summary>Elements are checked, at any depth.</summary>
    [Theory]
    [InlineData("var t: (int, string) = (\"x\", \"a\")")]
    [InlineData("var t: (int, (string, bool)) = (1, (\"x\", \"y\", 1))")]
    [InlineData("var t: (int, string)[] = [(1, \"a\"), (\"x\", \"b\")]")]
    public async Task An_element_mismatch_is_reported(string source)
        => Assert.Equal("tosh.runtime.annotation_conversion_failed", await DiagnosticCodeAsync(source));

    /// <summary>An element naming no known type is reported as such.</summary>
    [Fact]
    public async Task An_unknown_element_type_is_reported()
        => Assert.Equal(
            "tosh.runtime.annotation_unknown_type",
            await DiagnosticCodeAsync("var t: (int, Nope) = (1, \"a\")"));

    /// <summary>
    /// Controls: the syntax this shares tokens with still means what it did.
    /// </summary>
    /// <remarks>
    /// The lookahead now treats `(` after a colon as a possible type. These are the
    /// neighbouring forms that must not have been captured by that — a parenthesised
    /// expression, a call, and a tuple value with no annotation at all.
    /// </remarks>
    [Theory]
    [InlineData("var x = (1 + 2) * 3\necho $x", "9")]
    [InlineData("func f(n) {\n return $n * 2\n}\nvar y = (f 21)\necho $y", "42")]
    [InlineData("var t = (1, \"a\")\necho $t.Item2", "a")]
    [InlineData("var s: string = \"plain\"\necho $s", "plain")]
    [InlineData("var a: string[] = [\"x\"]\necho $a.Length", "1")]
    public async Task Neighbouring_syntax_is_unaffected(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}

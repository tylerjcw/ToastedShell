using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A closed generic is read, tested and checked as closed — <c>TOAST-0125</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three findings that were the same thing from different sides. `Two&lt;int, string&gt;(1, "x")`
/// never took the invocation path, because the call parenthesis was required to touch the
/// *name* and a type-argument list sits between them; the list was read as one tuple and
/// the arity check reported one argument where two were declared. `$x is Box&lt;int&gt;` did
/// not parse at all, because the operand of a type test was read as an expression and `&lt;`
/// became less-than. And an annotation naming `Box&lt;int&gt;` compared only the open name,
/// so it accepted a `Box&lt;string&gt;`.
/// </para>
/// <para>
/// Comparing rendered names is not enough on its own: `Box&lt;String&gt;` happens to match
/// `Box&lt;string&gt;` case-insensitively while `Box&lt;Int32&gt;` never matches `Box&lt;int&gt;`, so
/// `is` answered true for one closure and false for another with no difference between
/// them. A bound argument is compared by resolved type instead.
/// </para>
/// </remarks>
public sealed class ClosedGenericMatchingTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Box = "class Box<T>(v: T) { prop V: T = $v }\n";

    // ── F1: a call with a type-argument list ─────────────────────────────────

    private const string Functions =
        "func One<A>(a: A) -> string { return $\"one:{(type-of $a).Name}\" }\n"
        + "func Two<A, B>(a: A, b: B) -> string { return $\"two:{(type-of $a).Name}/{(type-of $b).Name}\" }\n"
        + "func Three<A, B, C>(a: A, b: B, c: C) -> string { return \"three\" }\n";

    [Theory]
    [InlineData("One<int>(1)", "one:Int32")]
    [InlineData("Two<int, string>(1, \"x\")", "two:Int32/String")]
    [InlineData("Two<int,string>(1, \"x\")", "two:Int32/String")]
    [InlineData("Three<int, string, bool>(1, \"x\", true)", "three")]
    // The forms that already worked.
    [InlineData("Two(1, \"x\")", "two:Int32/String")]
    [InlineData("Two<int, string> 1 \"x\"", "two:Int32/String")]
    public async Task A_type_argument_list_does_not_hide_the_call_parenthesis(
        string call,
        string expected)
        => Assert.Equal(expected, await RunAsync($"{Functions}{call}"));

    // ── F3: a closed type test ───────────────────────────────────────────────

    [Theory]
    [InlineData("$b is Box", "True")]
    [InlineData("$b is Box<int>", "True")]
    [InlineData("$b is Box<string>", "False")]
    [InlineData("$b is-not Box<string>", "True")]
    public async Task A_type_test_reads_and_compares_a_closed_name(string test, string expected)
        => Assert.Equal(expected, await RunAsync($"{Box}var b = new Box<int>(1)\n({test})"));

    [Fact]
    public async Task A_string_closure_is_told_apart_from_an_int_one()
    {
        // The case that made the old behaviour look arbitrary: it answered correctly for
        // `string` and incorrectly for `int`, because it was comparing rendered names.
        var source = $"{Box}var s = new Box<string>(\"a\")\nvar b = new Box<int>(1)\n"
            + "echo $\"{($s is Box<string>)}{($s is Box<int>)}{($b is Box<int>)}{($b is Box<string>)}\"";

        Assert.Equal("truefalsetruefalse", await RunAsync(source));
    }

    [Theory]
    // Comparisons are untouched — the break needs a type test and a glued angle bracket.
    [InlineData("(1 < 2)", "True")]
    [InlineData("var x = 1\nvar y = 2\n($x < $y)", "True")]
    [InlineData("(1 < 2 < 3)", "True")]
    [InlineData("(5 is int)", "True")]
    public async Task Comparisons_are_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    // ── A4: an annotation checks the closure ─────────────────────────────────

    [Fact]
    public async Task An_annotation_accepts_the_right_closure()
        => Assert.Equal("7", await RunAsync(
            $"{Box}func take(b: Box<int>) {{ return $b.V }}\nvar right = new Box<int>(7)\ntake $right"));

    [Theory]
    // Behind a variable so this is the runtime's answer rather than the binder's.
    [InlineData("func take(b: Box<int>) { return $b.V }\nvar w = new Box<string>(\"a\")\ntake $w")]
    [InlineData("var w = new Box<string>(\"a\")\nvar x: Box<int> = $w\n$x.V")]
    public async Task An_annotation_refuses_the_wrong_closure(string body)
        => await Assert.ThrowsAnyAsync<Exception>(() => RunAsync($"{Box}{body}"));

    [Theory]
    // Inheritance still satisfies it. A subclass that closed the base has no parameter list
    // of its own, so the closure is compared only where the name is the instance's own class
    // and the open-name match answers for an ancestor.
    [InlineData("class IntBox(v: int) extends Box<int>($v) { }\nvar b = new IntBox(3)\ntake $b", "3")]
    [InlineData("class OpenBox<T>(v: T) extends Box<T>($v) { }\nvar b = new OpenBox<int>(4)\ntake $b", "4")]
    public async Task A_subclass_still_satisfies_a_closed_annotation(string body, string expected)
        => Assert.Equal(expected, await RunAsync(
            $"{Box}func take(b: Box<int>) {{ return $b.V }}\n{body}"));

    [Fact]
    public async Task An_open_annotation_still_takes_any_closure()
        => Assert.Equal("9", await RunAsync(
            $"{Box}func take(b: Box) {{ return $b.V }}\nvar b = new Box<string>(\"9\")\ntake $b"));
}

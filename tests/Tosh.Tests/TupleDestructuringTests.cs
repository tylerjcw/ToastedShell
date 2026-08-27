using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Destructuring a tuple — <c>TS-P2-59</c>.
/// </summary>
/// <remarks>
/// <para>
/// Filed as "tuple destructuring does not parse". Measuring corrected that: the language already
/// had <c>var [a, b] = …</c> and <c>var { a, b } = …</c>, and <c>var [a, b] = (1, 2)</c> bound 1
/// and 2 perfectly well. Three narrower things were actually wrong.
/// </para>
/// <para>
/// <b>The parenthesised declaring form did not exist.</b> <c>(a, b) = …</c> already assigned to
/// existing variables and <c>(1, 2)</c> is how a tuple is written, so <c>var (a, b) = (1, 2)</c>
/// is the spelling a reader reaches for first — and the one that failed, with
/// <c>tosh.bind.unknown_command</c> for <c>var</c>, which says nothing about what is wrong.
/// </para>
/// <para>
/// <b>The two destructurings disagreed about tuples.</b> The declaring form accepted arrays,
/// lists, tuples and any enumerable; the assigning form accepted arrays alone. So
/// <c>var [a, b] = (1, 2)</c> bound 1 and 2 while <c>(a, b) = (1, 2)</c> bound the whole tuple to
/// <c>a</c> and null to <c>b</c>, silently. One rule now serves both (<c>TS-P1-24</c>).
/// </para>
/// <para>
/// <b><c>const</c> was accepted and ignored.</b> <c>const [A, B] = [1, 2]</c> declared two
/// perfectly mutable bindings, so <c>$A = 9</c> succeeded — pre-existing in the bracket form, and
/// something the new parenthesised form would have inherited.
/// </para>
/// </remarks>
public sealed class TupleDestructuringTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<ToshDiagnostic> RunForDiagnosticAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(source));
        return exception.Diagnostics[0];
    }

    // ── The declaring parenthesised form ───────────────────────────────────────

    [Fact]
    public async Task Var_with_parentheses_declares_and_binds()
    {
        Assert.Equal("1,2", await RunAsync("var (x, y) = (1, 2)\n$x\n$y"));
    }

    [Fact]
    public async Task Const_with_parentheses_declares_and_binds()
    {
        Assert.Equal("1,2", await RunAsync("const (A, B) = (1, 2)\n$A\n$B"));
    }

    [Fact]
    public async Task The_parenthesised_form_takes_an_array_too()
    {
        // The three bracket styles differ in what they *read from*, not in what they declare:
        // parentheses and brackets are both positional.
        Assert.Equal("1,2", await RunAsync("var (x, y) = [1, 2]\n$x\n$y"));
    }

    [Fact]
    public async Task The_parenthesised_form_takes_a_function_result()
    {
        Assert.Equal("7,8", await RunAsync(
            """
            func two() { return (7, 8) }
            var (p, q) = two()
            $p
            $q
            """));
    }

    [Fact]
    public async Task A_discard_binds_nothing()
    {
        Assert.Equal("1,3", await RunAsync("var (a, _, c) = (1, 2, 3)\n$a\n$c"));
    }

    // ── The two destructurings agree ───────────────────────────────────────────

    [Fact]
    public async Task Assigning_a_tuple_to_existing_variables_destructures_it()
    {
        // The silent one: `a` used to receive the whole tuple and `b` null.
        Assert.Equal("1,2", await RunAsync(
            """
            var a = 0
            var b = 0
            (a, b) = (1, 2)
            $a
            $b
            """));
    }

    [Fact]
    public async Task Both_forms_answer_the_same_for_the_same_value()
    {
        var declaring = await RunAsync("var (a, b) = (1, 2)\n$a\n$b");
        var assigning = await RunAsync("var a = 0\nvar b = 0\n(a, b) = (1, 2)\n$a\n$b");

        Assert.Equal(declaring, assigning);
    }

    [Fact]
    public async Task A_swap_still_works()
    {
        // The RHS is evaluated once and in full before any target is written, which is what
        // makes a swap mean what it looks like (`TS-P0-01`).
        Assert.Equal("2,1", await RunAsync(
            """
            var a = 1
            var b = 2
            (a, b) = ($b, $a)
            $a
            $b
            """));
    }

    [Fact]
    public async Task A_multi_value_pipeline_still_destructures()
    {
        Assert.Equal("1,2", await RunAsync(
            """
            var a = 0
            var b = 0
            (a, b) = echo 1 2
            $a
            $b
            """));
    }

    // ── Arity is checked ───────────────────────────────────────────────────────

    [Fact]
    public async Task Too_many_values_is_a_diagnostic()
    {
        var diagnostic = await RunForDiagnosticAsync(
            "var a = 0\nvar b = 0\n(a, b) = (1, 2, 3)");

        Assert.Equal("tosh.runtime.tuple_assignment_arity_mismatch", diagnostic.Code);
        Assert.Contains("2 targets", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("3 elements", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_few_values_is_a_diagnostic()
    {
        // Previously the surplus target was quietly set to null, which is the kind of silence
        // that turns a typo into a `null` reported three lines later.
        var diagnostic = await RunForDiagnosticAsync(
            "var a = 0\nvar b = 0\nvar c = 0\n(a, b, c) = (1, 2)");

        Assert.Equal("tosh.runtime.tuple_assignment_arity_mismatch", diagnostic.Code);
        Assert.Contains("not enough", diagnostic.Label!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_declaring_form_checks_arity_too()
    {
        var diagnostic = await RunForDiagnosticAsync("var (a, b) = (1, 2, 3)");

        Assert.Equal("tosh.runtime.tuple_assignment_arity_mismatch", diagnostic.Code);
    }

    [Theory]
    // An array is variable-length, and taking a prefix of one is a documented feature —
    // `var [first, second] = $fiveItems` is a worked example in the specification. Only a
    // tuple, whose shape is fixed, is held to its arity.
    [InlineData("var items = [10, 20, 30, 40, 50]\nvar [first, second] = $items\n$first", "10")]
    [InlineData("var items = [10, 20, 30]\nvar (a, b) = $items\n$a", "10")]
    [InlineData("var a = 0\nvar b = 0\n(a, b) = [1, 2, 3]\n$a\n$b", "1,2")]
    public async Task An_array_may_still_be_destructured_into_fewer_targets(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    // ── `const` means const, however it is spelled ─────────────────────────────

    [Theory]
    [InlineData("const (A, B) = (1, 2)")]
    [InlineData("const [A, B] = [1, 2]")]
    public async Task A_const_destructuring_refuses_reassignment(string declaration)
    {
        var diagnostic = await RunForDiagnosticAsync($"{declaration}\n$A = 9");

        Assert.Equal("tosh.runtime.const_reassignment", diagnostic.Code);
        Assert.Contains("A", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_const_record_destructuring_refuses_reassignment()
    {
        var diagnostic = await RunForDiagnosticAsync(
            "const {a, b} = {| a = 1, b = 2 |}\n$a = 9");

        Assert.Equal("tosh.runtime.const_reassignment", diagnostic.Code);
    }

    [Fact]
    public async Task A_var_destructuring_stays_mutable()
    {
        // The control that keeps the rule from becoming "all destructured bindings are const".
        Assert.Equal("9", await RunAsync("var (a, b) = (1, 2)\n$a = 9\n$a"));
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Theory]
    [InlineData("var [a, b] = [1, 2]\n$a\n$b", "1,2")]
    [InlineData("var [a, b] = (1, 2)\n$a\n$b", "1,2")]
    [InlineData("var {a, b} = {| a = 1, b = 2 |}\n$a\n$b", "1,2")]
    [InlineData("var t = (1, 2)\n$t.Item1\n$t.Item2", "1,2")]
    public async Task The_existing_forms_are_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_parenthesised_expression_is_still_an_expression()
    {
        // The parse has to tell `var (a, b) = …` from an ordinary parenthesised value, and the
        // difference is only the keyword in front.
        Assert.Equal("3", await RunAsync("var x = (1 + 2)\n$x"));
    }

    [Fact]
    public async Task A_single_name_in_parentheses_binds_one_element()
    {
        Assert.Equal("1", await RunAsync("var (a) = [1]\n$a"));
    }
}

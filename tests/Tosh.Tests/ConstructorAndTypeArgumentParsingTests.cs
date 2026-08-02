using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Base constructor arguments, and type arguments inside parentheses — <c>TS-P2-50</c>.
/// </summary>
/// <remarks>
/// <para>
/// The item was filed as one defect — "a comma inside <c>&lt;…&gt;</c> immediately followed by
/// <c>(</c> mis-parses" — and was two, sharing only a symptom. Both spellings in the report failed,
/// and narrowing each one separately is what separated them.
/// </para>
/// <para>
/// <b>Base constructor arguments had nothing to do with generics.</b>
/// <c>extends P&lt;X, Y&gt;($a, $b)</c> failed, and so did <c>extends P0($a, $b)</c> with no type
/// argument anywhere in it, while <c>extends P&lt;int, int&gt;($a)</c> parsed. The trigger was the
/// second *argument*: each one was read as a pipeline running until the close paren, so the
/// separating comma was neither a terminator the pipeline recognised nor a valid continuation of
/// it. A base constructor could be given exactly one argument. Arguments are now read the way
/// every other parenthesised argument list is read.
/// </para>
/// <para>
/// <b>The tuple confusion was real, and was the other half.</b> In
/// <c>(new P&lt;int, int&gt;(3, 4)).A</c> the comma between the type arguments read as a top-level
/// separator, so the parenthesised expression was parsed as a tuple and the member access reported
/// <c>Member 'A' was not found on type 'ToshTuple'</c>. One type argument parsed, having no comma
/// to misread. The scanner that decides tuple-or-not now steps over a type argument list, which
/// its two sibling scanners already did — the rule existed in three places and was right in two.
/// </para>
/// </remarks>
public sealed class ConstructorAndTypeArgumentParsingTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string TwoParameterBase =
        """
        class P<T1, T2>(a: T1, b: T2) {
            prop A: T1 = $a
            prop B: T2 = $b
        }
        """;

    // ── A base constructor takes as many arguments as it declares ──────────────

    [Fact]
    public async Task Two_base_constructor_arguments_parse_without_any_generics()
    {
        // The narrowing case: no type arguments are involved, and it failed just the same.
        Assert.Equal("1,2", await RunAsync(
            """
            class P0(a: int, b: int) {
                prop A = $a
                prop B = $b
            }
            class Q(a: int, b: int) extends P0($a, $b) { }
            var q = new Q(1, 2)
            $q.A
            $q.B
            """));
    }

    [Fact]
    public async Task Two_base_constructor_arguments_parse_with_type_arguments()
    {
        Assert.Equal("1,2", await RunAsync(
            $$"""
            {{TwoParameterBase}}
            class Q(a: int, b: int) extends P<int, int>($a, $b) { }
            var q = new Q(1, 2)
            $q.A
            $q.B
            """));
    }

    [Fact]
    public async Task A_subclass_passes_its_own_type_parameters_and_arguments_through()
    {
        Assert.Equal("1,s", await RunAsync(
            $$"""
            {{TwoParameterBase}}
            class Q<X, Y>(a: X, b: Y) extends P<X, Y>($a, $b) { }
            var q = new Q<int, string>(1, "s")
            $q.A
            $q.B
            """));
    }

    [Fact]
    public async Task Three_base_constructor_arguments_parse()
    {
        Assert.Equal("6", await RunAsync(
            """
            class P3(a: int, b: int, c: int) { prop S = (($a + $b) + $c) }
            class Q(a: int, b: int, c: int) extends P3($a, $b, $c) { }
            (new Q(1, 2, 3)).S
            """));
    }

    [Fact]
    public async Task Base_constructor_arguments_may_be_expressions()
    {
        Assert.Equal("2,4", await RunAsync(
            """
            class P0(a: int, b: int) {
                prop A = $a
                prop B = $b
            }
            class Q(a: int, b: int) extends P0(($a + 1), ($b * 2)) { }
            var q = new Q(1, 2)
            $q.A
            $q.B
            """));
    }

    [Fact]
    public async Task Two_literal_base_constructor_arguments_parse()
    {
        // Literals rather than parameter references, to show the defect was in reading the
        // argument *list* and not in what the arguments happened to be.
        Assert.Equal("1", await RunAsync(
            """
            class P0(a: int, b: int) { prop A = $a }
            class Q extends P0(1, 2) { }
            (new Q()).A
            """));
    }

    [Theory]
    // The controls: the arities that already worked, which the new parsing had to preserve. One
    // argument and none — anything with two was broken, so nothing with two belongs here.
    [InlineData("class P1(a: int) { prop A = $a }\nclass Q(a: int) extends P1($a) { }\n(new Q(7)).A", "7")]
    [InlineData("class P1 { prop A = 1 }\nclass Q extends P1 { }\n(new Q()).A", "1")]
    public async Task Base_constructor_forms_that_already_worked_are_unchanged(
        string source,
        string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    // ── Type arguments inside parentheses are not a tuple ──────────────────────

    [Fact]
    public async Task A_parenthesised_construction_with_two_type_arguments_is_not_a_tuple()
    {
        Assert.Equal("3", await RunAsync($"{TwoParameterBase}\n(new P<int, int>(3, 4)).A"));
    }

    [Fact]
    public async Task Type_arguments_of_different_types_are_carried_through()
    {
        Assert.Equal("s", await RunAsync($"{TwoParameterBase}\n(new P<int, string>(3, \"s\")).B"));
    }

    [Fact]
    public async Task Three_type_arguments_parse_inside_parentheses()
    {
        Assert.Equal("1", await RunAsync(
            """
            class T3<A, B, C>(a: A, b: B, c: C) { prop V: A = $a }
            (new T3<int, int, int>(1, 2, 3)).V
            """));
    }

    [Theory]
    // Tuples are what the scanner exists to find, and still are. A type argument list is stepped
    // over; everything else it used to treat as a separator, it still does.
    [InlineData("var t = (1, 2)\n$t.Count", "2")]
    [InlineData("var a = 1\nvar t = ($a, ($a + 1))\n$t.Item2", "2")]
    [InlineData("var a = 1\nvar b = 2\nvar t = (($a < $b), ($b > $a))\n$t.Item1", "True")]
    public async Task Tuples_are_still_recognised(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Theory]
    // Comparison operators are the reason angle brackets are ambiguous at all, so they are pinned
    // beside the type-argument cases rather than assumed.
    [InlineData("var a = 1\nvar b = 2\n($a < $b)", "True")]
    [InlineData("var a = 1\nvar b = 2\n($b > $a)", "True")]
    public async Task Comparisons_in_parentheses_still_parse(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task One_type_argument_in_parentheses_is_unchanged()
    {
        // The control that made the two-argument case stand out: no comma, nothing to misread.
        Assert.Equal("3", await RunAsync(
            "class P1<T>(a: T) { prop A: T = $a }\n(new P1<int>(3)).A"));
    }
}

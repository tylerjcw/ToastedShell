using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A type argument naming a ToastScript type is remembered and checked — <c>TOAST-0125</c>.
/// </summary>
/// <remarks>
/// <para>
/// A ToastScript class has no CLR type of its own, so `Holder&lt;Circle&gt;` resolved its
/// argument to null. That null was read as "accept anything", which made the annotation
/// unenforceable — a `Holder&lt;Circle&gt;` took a `Square`, an `int`, anything at all. The
/// name was discarded too, so the instance could not even say what it had been closed over
/// and `type-of` answered `Holder&lt;T&gt;`.
/// </para>
/// <para>
/// Resolving the name to a CLR type instead is exactly what `TS-P2-39` was: the search
/// reached every loaded assembly and found an unrelated type sharing the name, which made
/// the suite's longest-running flake. So the name is kept and checked *nominally*, against
/// the value's own declaration, through the same contract `is` uses — which is why a
/// subclass and an implemented interface both satisfy it.
/// </para>
/// <para>
/// Only names the script declared are enforced. `array`, `list` and `dict` are named shell
/// types as well, but they name a CLR shape rather than a declaration; refusing what cannot
/// be checked that way would turn an unenforced annotation into a wrong error.
/// </para>
/// </remarks>
public sealed class NominalTypeArgumentTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Shapes =
        "class Circle(r) { prop R = $r }\n"
        + "class Square(s) { prop S = $s }\n"
        + "class Holder<T>(v: T) { prop V: T = $v }\n";

    [Theory]
    [InlineData("new Holder<Circle>(new Circle(1))", "Holder<Circle>")]
    [InlineData("new Holder<array>([1])", "Holder<array>")]
    [InlineData("new Holder<Holder<int>>(new Holder<int>(1))", "Holder<Holder<int>>")]
    public async Task The_name_it_was_closed_over_survives(string construction, string expected)
        => Assert.Equal(expected, await RunAsync($"{Shapes}var h = {construction}\n(type-of $h).Name"));

    [Fact]
    public async Task A_matching_value_is_accepted()
        => Assert.Equal("2", await RunAsync($"{Shapes}(new Holder<Circle>(new Circle(2))).V.R"));

    [Theory]
    [InlineData("new Holder<Circle>(new Square(2))")]
    [InlineData("new Holder<Circle>(5)")]
    [InlineData("new Holder<Circle>(\"a\")")]
    public async Task A_value_of_another_declared_type_is_refused(string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync($"{Shapes}{construction}"));
        Assert.Contains("is not a 'Circle'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subclass_satisfies_it()
    {
        var source =
            "class Shape(n) { prop N = $n }\n"
            + "class Dot(n) extends Shape($n) { }\n"
            + "class Holder<T>(v: T) { prop V: T = $v }\n"
            + "(new Holder<Shape>(new Dot(3))).V.N";

        Assert.Equal("3", await RunAsync(source));
    }

    [Fact]
    public async Task An_implemented_interface_satisfies_it()
    {
        var source =
            "interface Drawable { func Draw() -> string }\n"
            + "class Sprite() fulfills Drawable { func Draw() -> string => \"drawn\" }\n"
            + "class Holder<T>(v: T) { prop V: T = $v }\n"
            + "(new Holder<Drawable>(new Sprite())).V.Draw()";

        Assert.Equal("drawn", await RunAsync(source));
    }

    [Fact]
    public async Task A_method_parameter_is_checked_too()
    {
        // The property caught the constructor case on its own; a method parameter had no
        // check at all until the binding carried a name to check against.
        var source =
            Shapes
            + "class Bag<T>(seed: T) {\n"
            + "    prop Seed: T = $seed\n"
            + "    func Take(v: T) -> string { return \"took\" }\n"
            + "}\n"
            + "var bag = new Bag<Circle>(new Circle(1))\n"
            + "$bag.Take(new Square(2))";

        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(source));
        Assert.Contains("is not a 'Circle'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    // An alias names a CLR shape, not a declaration, so it stays unchecked rather than
    // becoming a wrong error.
    [InlineData("new Holder<array>([1, 2])")]
    [InlineData("new Holder<list>([1, 2])")]
    public async Task An_alias_argument_is_not_turned_into_a_nominal_check(string construction)
        => Assert.Equal("Holder", (await RunAsync($"{Shapes}var h = {construction}\n(type-of $h).Name")).Split('<')[0]);

    [Theory]
    // A CLR-typed argument is unaffected: that path was already strict.
    [InlineData("new Holder<int>(5)", "5")]
    [InlineData("new Holder<string>(\"s\")", "s")]
    public async Task A_clr_typed_argument_is_unchanged(string construction, string expected)
        => Assert.Equal(expected, await RunAsync($"{Shapes}({construction}).V"));
}

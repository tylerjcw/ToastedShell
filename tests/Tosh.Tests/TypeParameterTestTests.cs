using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>T is double</c> — a test on the type a type parameter is bound to
/// (<c>TOAST-0131</c>).
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the language could reach a type parameter's binding: <c>nameof(T)</c> and
/// a bare <c>T</c> both gave the string <c>"T"</c>, the parameter's own name. The
/// runtime knew — <c>EnterTypeParameterBindings</c> puts it in scope for the body's
/// duration — and only the surface was missing.
/// </para>
/// <para>
/// It is answered from the <em>syntax</em>, before the left operand is evaluated,
/// because evaluating a bareword <c>T</c> yields the string <c>"T"</c> and that is
/// indistinguishable from a genuine string of that name.
/// </para>
/// <para>
/// The load-bearing decision is that once the left side is known to name a type
/// parameter, the question is answered here whatever the outcome. Declining would send
/// it to the value comparison, where <c>"T" is Thing</c> comes back a confident, wrong
/// <c>false</c> — which is how the ToastScript-class case was caught.
/// </para>
/// </remarks>
public sealed class TypeParameterTestTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    private static async Task<object?> OneAsync(string script) =>
        Assert.Single(await RunAsync(script));

    [Theory]
    [InlineData("func k<T>() => (T is double)\nk<double>", true)]
    [InlineData("func k<T>() => (T is double)\nk<int>", false)]
    [InlineData("func k<T>() => (T is string)\nk<string>", true)]
    [InlineData("func k<T>() => (T is-not double)\nk<int>", true)]
    [InlineData("func k<T>() => (T is-not double)\nk<double>", false)]
    // Assignability, not identity: the same question `$value is Base` asks.
    [InlineData("func k<T>() => (T is object)\nk<int>", true)]
    public async Task A_function_type_parameter_answers_for_its_binding(string script, bool expected)
    {
        Assert.Equal(expected, await OneAsync(script));
    }

    private const string BoxedThing = """
        class Thing(v) { prop V = $v }
        class Other(v) { prop V = $v }
        class Box<T>(v: T) {
            prop V: T = $v
            func Which(name) {
                if ($name == "double") { return (T is double) }
                if ($name == "Thing")  { return (T is Thing) }
                return (T is Other)
            }
        }
        """;

    [Theory]
    [InlineData("(new Box<double>(1.0)).Which(\"double\")", true)]
    [InlineData("(new Box<int>(1)).Which(\"double\")", false)]
    // A type argument naming a ToastScript class has no CLR type, so it is recorded by
    // name. Answering it is what stops a confident, wrong `false`.
    [InlineData("(new Box<Thing>((new Thing(1)))).Which(\"Thing\")", true)]
    [InlineData("(new Box<Thing>((new Thing(1)))).Which(\"Other\")", false)]
    public async Task A_class_type_parameter_answers_for_its_binding(string call, bool expected)
    {
        Assert.Equal(expected, await OneAsync(BoxedThing + "\n" + call));
    }

    /// <summary>The shape this was asked for: dispatch on the closure.</summary>
    [Theory]
    [InlineData("double", "double precision")]
    [InlineData("float", "single precision")]
    [InlineData("int", "integral")]
    [InlineData("string", "something else")]
    public async Task A_generic_can_dispatch_on_what_it_is_closed_over(string closure, string expected)
    {
        var result = await OneAsync(
            """
            class Point2D<T>(x: T, y: T) {
                prop X: T = $x
                static func Describe<T>() {
                    if (T is double) { return "double precision" }
                    if (T is float)  { return "single precision" }
                    if (T is int)    { return "integral" }
                    return "something else"
                }
            }
            """
            + $"\nPoint2D.Describe<{closure}>()");

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// The controls. A bareword that is not a type parameter, and a string that happens to
    /// be spelled "T", must both still take the ordinary value comparison — the test is
    /// keyed on the name being bound, not on its spelling.
    /// </summary>
    [Theory]
    [InlineData("var s = \"T\"\n($s is string)", true)]
    [InlineData("var s = \"T\"\n($s is int)", false)]
    [InlineData("func k<T>() => (Nonexistent is double)\nk<int>", false)]
    public async Task An_ordinary_value_test_is_unaffected(string script, bool expected)
    {
        Assert.Equal(expected, await OneAsync(script));
    }

    /// <summary>
    /// And a type parameter on the *right* is untouched by this: it was never the question
    /// being answered, and changing it is not in scope here.
    /// </summary>
    [Fact]
    public async Task A_type_parameter_on_the_right_is_unchanged()
    {
        var result = await OneAsync(
            "class Box<T>(v: T) {\n    prop V: T = $v\n    func W() => ($this.V is T)\n}\n"
            + "(new Box<int>(1)).W()");

        Assert.Equal(false, result);
    }
}

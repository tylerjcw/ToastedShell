using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Writing a widget in TōSh, and the checker agreeing that it is one.
/// </summary>
/// <remarks>
/// <para>
/// The framework's own <c>TuiWidget</c> is the extension point: a class that extends it
/// overrides <c>MeasureCore</c> and <c>Draw</c>, and the layout engine calls them. That is
/// what <c>extends</c> emitting a real subclass buys — the container has no idea one of its
/// children was written in a script.
/// </para>
/// <para>
/// The checker had to be told. It compared the declared class against the parameter type
/// and saw nothing in common, so <c>TuiStack.Add</c> was warned about for a widget it then
/// accepted and rendered. A warning on code that works is how people learn to stop reading
/// them.
/// </para>
/// </remarks>
public sealed class ClrBaseWidgetAuthoringTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public ClrBaseWidgetAuthoringTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Diagnose(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<widget-test>");

        return TypeChecker.Check(Lowerer.Lower(parse, _runtime.Commands));
    }

    private bool Mismatches(string source) =>
        Diagnose(source).Any(d => d.Code == "tosh.type.mismatch");

    private const string Banner = """
        class Banner(text) extends Tosh.Tui.Widgets.TuiWidget {
            prop Text = $text
            func MeasureCore(constraints) { return new Tosh.Tui.TuiSize($this.Text.Length, 1) }
            func Draw(surface) { $surface.DrawText(0, 0, $this.Text, new Tosh.Tui.Rendering.TuiStyle()) }
        }
        """;

    /// <summary>A class extending a widget is accepted where a widget is wanted.</summary>
    [Fact]
    public void A_class_extending_a_widget_is_accepted_as_one()
        => Assert.False(Mismatches($$"""
            {{Banner}}
            var stack = new Tosh.Tui.Widgets.TuiStack()
            var added = $stack.Add(new Banner("hello"))
            """));

    /// <summary>
    /// The base may be further up the chain.
    /// </summary>
    /// <remarks>
    /// For <c>class Leaf extends Mid</c> over <c>class Mid extends TuiWidget</c>, the leaf
    /// names no CLR type at all — reading only the class in hand would warn on it.
    /// </remarks>
    [Fact]
    public void A_widget_base_two_levels_up_is_still_a_widget()
        => Assert.False(Mismatches($$"""
            {{Banner}}
            class LoudBanner(text) extends Banner($text) { }
            var stack = new Tosh.Tui.Widgets.TuiStack()
            var added = $stack.Add(new LoudBanner("hello"))
            """));

    /// <summary>
    /// A class that is not a widget is still refused.
    /// </summary>
    /// <remarks>
    /// The check has to survive being taught about CLR bases; silencing the false positive
    /// by weakening it into silence would cost more than it saved.
    /// </remarks>
    [Fact]
    public void A_class_that_extends_nothing_is_still_refused()
        => Assert.True(Mismatches("""
            class NotAWidget { prop X = 1 }
            var stack = new Tosh.Tui.Widgets.TuiStack()
            var added = $stack.Add(new NotAWidget())
            """));

    /// <summary>An ordinary value is still refused.</summary>
    [Fact]
    public void A_plain_value_is_still_refused()
        => Assert.True(Mismatches("""
            var stack = new Tosh.Tui.Widgets.TuiStack()
            var added = $stack.Add("a plain string")
            """));

    /// <summary>
    /// And the widget renders, inside a container, through the framework's own pipeline.
    /// </summary>
    /// <remarks>
    /// The point of all of it: <c>Measure</c> reaches the script's <c>MeasureCore</c>,
    /// <c>DrawChild</c> reaches its <c>Draw</c>, and the built-in rows above and below it are
    /// unaware that one of their siblings is not a CLR widget at all.
    /// </remarks>
    [Fact]
    public async Task A_tosh_widget_renders_inside_a_framework_container()
    {
        var engine = new ToshEngine(_runtime.Language);

        var results = await engine.ExecuteToListAsync($$"""
            {{Banner}}
            var stack = new Tosh.Tui.Widgets.TuiStack()
            var a = $stack.Add(new Tosh.Tui.Widgets.TuiTextWidget("built-in row"))
            var b = $stack.Add(new Banner("tosh-authored row"))
            var c = $stack.Add(new Tosh.Tui.Widgets.TuiTextWidget("another built-in"))

            var size = $stack.Measure(new Tosh.Tui.Widgets.TuiConstraints(30, 5))
            var d = $stack.Arrange(new Tosh.Tui.TuiRect(0, 0, 30, 3))
            var buffer = new Tosh.Tui.Rendering.TuiBuffer(new Tosh.Tui.TuiSize(30, 3))
            var e = $stack.Draw(new Tosh.Tui.Rendering.TuiSurface($buffer, new Tosh.Tui.TuiRect(0, 0, 30, 3)))

            echo $buffer.RowText(0).TrimEnd()
            echo $buffer.RowText(1).TrimEnd()
            echo $buffer.RowText(2).TrimEnd()
            """);

        Assert.Equal(
            ["built-in row", "tosh-authored row", "another built-in"],
            results.OfType<string>());
    }
}

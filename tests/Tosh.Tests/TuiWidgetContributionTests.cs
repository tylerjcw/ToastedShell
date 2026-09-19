using Tosh.Language;
using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Naming a widget of your own in markup.
/// </summary>
/// <remarks>
/// <para>
/// A script can write a widget and hand it to a container with <c>new</c>, because a
/// declared class that extends a CLR type is one. Markup was the half that could not: a
/// node's name is looked up in the registry, the registry held only what the framework put
/// there, and nothing could add to it from outside. The same widget was usable one way and
/// unnameable the other.
/// </para>
/// <para>
/// Contributions live apart from any one registry because a registry is built fresh for
/// each screen, and a contribution has to outlive the screen that did not yet exist when it
/// was made.
/// </para>
/// </remarks>
[Collection("tui-contributions")]
public sealed class TuiWidgetContributionTests : IDisposable
{
    public TuiWidgetContributionTests() => TuiWidgetContributions.Clear();

    // Global by nature, so it must not leak into the next test.
    public void Dispose() => TuiWidgetContributions.Clear();

    private sealed class Banner(string text) : TuiWidget
    {
        protected override TuiSize MeasureCore(TuiConstraints constraints) => new(text.Length, 1);

        public override void Draw(TuiSurface surface) => surface.DrawText(0, 0, text, new TuiStyle());
    }

    private static string[] Render(object tree, int width, int height)
    {
        var root = TuiTreeBuilder.Build(tree, null, out _);

        root.Measure(new TuiConstraints(width, height));
        root.Arrange(new TuiRect(0, 0, width, height));

        var buffer = new TuiBuffer(new TuiSize(width, height));

        root.Draw(new TuiSurface(buffer, new TuiRect(0, 0, width, height)));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static Dictionary<string, object?> Node(string name, object? value) =>
        new(StringComparer.OrdinalIgnoreCase) { [name] = value };

    /// <summary>A contributed widget can be named in a tree, beside the built-ins.</summary>
    [Fact]
    public void A_contributed_widget_is_nameable_in_markup()
    {
        TuiWidgetContributions.Contribute("banner", (spec, _) => new Banner(spec.PrimaryText() ?? string.Empty));

        var rows = Render(
            Node("Column", new object?[]
            {
                Node("Text", "built-in above"),
                Node("Banner", "contributed"),
                Node("Text", "built-in below"),
            }),
            30,
            3);

        Assert.Equal(["built-in above", "contributed", "built-in below"], rows);
    }

    /// <summary>A withdrawn name is not known again.</summary>
    [Fact]
    public void A_withdrawn_widget_is_no_longer_nameable()
    {
        TuiWidgetContributions.Contribute("banner", (spec, _) => new Banner(spec.PrimaryText() ?? string.Empty));

        Assert.True(TuiWidgetContributions.Withdraw("banner"));
        Assert.False(TuiWidgetContributions.Withdraw("banner"));

        Assert.Throws<ArgumentException>(() => Render(Node("Banner", "gone"), 20, 1));
    }

    /// <summary>
    /// Contributing a name twice keeps the second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A contribution also takes precedence over a built-in of the same name — refusing that
    /// would be a rule that equally stops someone replacing a widget on purpose, which is
    /// the more useful of the two things to allow.
    /// </para>
    /// <para>
    /// That half is deliberately not asserted here. Contributions are process-wide, and a
    /// dozen other test classes build markup trees in parallel with this one; shadowing
    /// <c>text</c> for the length of a render would sometimes replace theirs too. A race
    /// that usually passes is worse than a gap in coverage, because it is charged to
    /// whichever test happened to be running.
    /// </para>
    /// </remarks>
    [Fact]
    public void Contributing_a_name_twice_keeps_the_second()
    {
        TuiWidgetContributions.Contribute("banner", (_, _) => new Banner("first"));
        TuiWidgetContributions.Contribute("banner", (_, _) => new Banner("second"));

        Assert.Equal(["second"], Render(Node("Banner", "ignored"), 20, 1));
    }

    /// <summary>A name nobody contributed is still an error.</summary>
    [Fact]
    public void An_unknown_name_is_still_refused()
        => Assert.Throws<ArgumentException>(() => Render(Node("Nonesuch", "x"), 20, 1));

    /// <summary>
    /// And the whole of it from a script: a declared class, named in markup.
    /// </summary>
    /// <remarks>
    /// The factory is a tōsh lambda handed to a CLR delegate parameter, and it answers with
    /// a <c>ToshClassInstance</c> where a <c>TuiWidget</c> is required — both of which work
    /// only because a declared class extending a CLR type is one at the boundary.
    /// </remarks>
    [Fact]
    public async Task A_script_can_name_its_own_class_in_markup()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("""
            class Banner(text) extends Tosh.Tui.Widgets.TuiWidget {
                prop Text = $text
                func MeasureCore(c) { return new Tosh.Tui.TuiSize($this.Text.Length, 1) }
                func Draw(s) { $s.DrawText(0, 0, $this.Text, new Tosh.Tui.Rendering.TuiStyle()) }
            }

            var make = func(spec, context) => (new Banner($spec.PrimaryText()))
            Tosh.Tui.Declarative.TuiWidgetContributions::Contribute("banner", $make)

            var tree = {| Column = [ {| Text = "above" |}, {| Banner = "scripted" |} ] |}
            var root = Tosh.Tui.Declarative.TuiTreeBuilder::Build($tree, null)
            var m = $root.Measure(new Tosh.Tui.Widgets.TuiConstraints(20, 2))
            var a = $root.Arrange(new Tosh.Tui.TuiRect(0, 0, 20, 2))
            var buffer = new Tosh.Tui.Rendering.TuiBuffer(new Tosh.Tui.TuiSize(20, 2))
            var d = $root.Draw(new Tosh.Tui.Rendering.TuiSurface($buffer, new Tosh.Tui.TuiRect(0, 0, 20, 2)))
            echo $buffer.RowText(0).TrimEnd()
            echo $buffer.RowText(1).TrimEnd()
            """);

        Assert.Equal(["above", "scripted"], results.OfType<string>());
    }
}

using System.Dynamic;
using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Record trees into widget trees — the "markup" half of the declarative surface.
/// </summary>
public sealed class TuiTreeBuilderTests
{
    /// <summary>A record literal, as the language produces one.</summary>
    private static IDictionary<string, object?> Node(params (string Key, object? Value)[] fields)
    {
        var record = new ExpandoObject();
        var dictionary = (IDictionary<string, object?>)record;

        foreach (var (key, value) in fields)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    [Fact]
    public void The_key_that_names_a_widget_carries_its_main_argument()
    {
        var widget = TuiTreeBuilder.Build(Node(("Text", "hello")));

        Assert.IsType<TuiTextWidget>(widget);
        Assert.Equal(["hello"], Render(widget, 10, 1));
    }

    [Fact]
    public void Other_keys_are_properties_of_that_widget()
    {
        var widget = (TuiTextWidget)TuiTreeBuilder.Build(Node(("Text", "hi"), ("Bold", true), ("Foreground", "red")));

        Assert.Equal(TuiTextAttributes.Bold, widget.Style.Attributes);
        Assert.Equal("red", widget.Style.Foreground);
    }

    [Fact]
    public void An_id_is_kept_so_a_result_can_be_keyed_by_it()
    {
        var widget = TuiTreeBuilder.Build(Node(("Field", "Width"), ("Id", "Width"), ("Value", "2")));

        // The id is on the row, which answers with what its input holds — so a caller
        // names the thing they wrote rather than a widget synthesised inside it.
        var field = Assert.IsType<TuiField>(widget);

        Assert.Equal("Width", field.Id);
        Assert.Equal("2", field.Text);
        Assert.Equal("2", field.Value);
    }

    [Fact]
    public void A_column_nests_its_children()
    {
        var widget = TuiTreeBuilder.Build(Node(("Column", new object?[]
        {
            Node(("Text", "top")),
            Node(("Text", "bottom")),
        })));

        Assert.Equal(["top", "bottom"], Render(widget, 10, 2));
    }

    [Fact]
    public void A_row_places_its_children_side_by_side()
    {
        var widget = TuiTreeBuilder.Build(Node(("Row", new object?[]
        {
            Node(("Text", "LL"), ("Size", 2)),
            Node(("Text", "RR"), ("Size", "*")),
        })));

        Assert.Equal(["LLRR"], Render(widget, 4, 1));
    }

    [Fact]
    public void A_size_may_be_cells_a_star_or_auto()
    {
        var widget = TuiTreeBuilder.Build(Node(("Row", new object?[]
        {
            Node(("Text", "ab"), ("Size", "auto")),
            Node(("Text", "cdefgh"), ("Size", "2*")),
            Node(("Text", "ij"), ("Size", 2)),
        })));

        Assert.Equal(["abcde\u2026ij"], Render(widget, 8, 1));
    }

    [Fact]
    public void A_bare_value_among_children_is_text()
    {
        var widget = TuiTreeBuilder.Build(Node(("Column", new object?[] { "one", "two" })));

        Assert.Equal(["one", "two"], Render(widget, 5, 2));
    }

    [Fact]
    public void A_box_draws_a_border_round_what_it_holds()
    {
        var widget = TuiTreeBuilder.Build(
            Node(("Box", new object?[] { Node(("Text", "in")) }), ("Title", "T")));

        Assert.Equal(["╭ T ──╮", "│in   │", "╰─────╯"], Render(widget, 7, 3));
    }

    [Fact]
    public void A_list_needs_no_wrapper_to_scroll()
    {
        // A list scrolls itself. Wrapping one was the ritual this is meant to remove, and
        // the wrapper caused more trouble than it saved — see TuiList.Offset.
        var widget = TuiTreeBuilder.Build(Node(("List", new object?[] { "a", "b", "c" })));

        Assert.IsType<TuiList>(widget);
    }

    [Fact]
    public void A_widget_built_in_code_can_sit_in_a_record_tree()
    {
        var made = new TuiTextWidget("made in code");

        var widget = TuiTreeBuilder.Build(Node(("Column", new object?[] { made, Node(("Text", "declared")) })));

        Assert.Equal(["made in code", "declared"], Render(widget, 12, 2));
    }

    /// <summary>
    /// The key that names the widget need not be the first one written.
    /// </summary>
    /// <remarks>
    /// A nested tree reads better with the properties above the children — the title and
    /// the size of a pane before the pane's contents, rather than after a block of them.
    /// The builder looks for the first key it *recognises*, not the first key, and this is
    /// what keeps that true.
    /// </remarks>
    [Fact]
    public void The_widget_key_can_come_after_the_properties()
    {
        var widget = TuiTreeBuilder.Build(Node(
            ("Title", "Inputs"),
            ("Size", "2*"),
            ("Box", new object?[] { Node(("Text", "inside")) })));

        var border = Assert.IsType<TuiBorder>(widget);

        Assert.Equal("Inputs", border.Title);
        Assert.Equal(TuiLength.Star(2), border.Size);
        Assert.Equal(["╭ Inputs ─╮", "│inside   │", "╰─────────╯"], Render(border, 11, 3));
    }

    [Fact]
    public void A_node_naming_nothing_says_what_it_could_have_named()
    {
        var error = Assert.Throws<ArgumentException>(
            () => TuiTreeBuilder.Build(Node(("Nonsense", "x"))));

        Assert.Contains("No widget was named", error.Message, StringComparison.Ordinal);
        Assert.Contains("list", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_registry_is_open_so_a_new_widget_can_be_added()
    {
        var registry = TuiWidgetRegistry.CreateDefault()
            .Register("banner", static (spec, _) => new TuiTextWidget($"** {spec.Primary} **"));

        var widget = TuiTreeBuilder.Build(Node(("Banner", "hi")), registry);

        Assert.Equal(["** hi **"], Render(widget, 10, 1));
    }

    [Fact]
    public void A_tree_gets_its_children_display_and_identity_from_the_node()
    {
        object?[] kids = ["a", "b"];

        var widget = TuiTreeBuilder.Build(
            Node(
                ("Tree", "root"),
                ("Children", Callable(_ => kids)),
                ("Display", Callable(node => $"<{node}>")),
                ("Key", Callable(node => $"key:{node}"))),
            registry: null,
            invoke: (callable, argument) => ((FakeCallable)callable).Body(argument),
            out _);

        var tree = Assert.IsType<TuiTree>(widget);

        tree.Rebuild();
        Assert.Equal(["▸ <root>"], Render(tree, 20, 1));

        // The fake answers every node with the same two children, so they look like
        // branches too — which is the lookahead working, not a mistake.
        tree.Expand(tree.Root);
        Assert.Equal(["▾ <root>", "├▸ <a>", "└▸ <b>"], Render(tree, 20, 3));
    }

    private static IShellCallable Callable(Func<object?, object?> body) => new FakeCallable(body);

    private sealed class FakeCallable(Func<object?, object?> body) : IShellCallable
    {
        public Func<object?, object?> Body { get; } = body;

        public string CallableName => "fake";

        public int RequiredParameterCount => 1;

        public int? MaximumParameterCount => 1;

        public IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
            => throw new NotSupportedException("Invoked through the screen's invoker instead.");
    }

    private static IEnumerable<TuiWidget> Descendants(TuiWidget widget)
    {
        yield return widget;

        foreach (var child in widget.Children)
        {
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}

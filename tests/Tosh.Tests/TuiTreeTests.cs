using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A tree of anything, opened a node at a time.
/// </summary>
public sealed class TuiTreeTests
{
    /// <summary>A node that knows its own path, so identity does not depend on its label.</summary>
    private sealed record Node(string Path, string Name, params Node[] Children);

    private static Node Sample() => new(
        "/",
        "root",
        new Node("/a", "alpha", new Node("/a/1", "one"), new Node("/a/2", "two")),
        new Node("/b", "beta"));

    private static TuiTree Tree(bool showRoot = true)
    {
        var tree = new TuiTree(Sample())
        {
            ChildrenOf = node => ((Node)node!).Children,
            Identify = node => ((Node)node!).Path,
            Display = node => ((Node)node!).Name,
            ShowRoot = showRoot,
        };

        tree.Arrange(new TuiRect(0, 0, 30, 10));
        return tree;
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

    private static TuiTree Focused(TuiTree tree)
    {
        new TuiFocus(tree).Focus(tree);
        return tree;
    }

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    [Fact]
    public void A_closed_tree_shows_only_its_root()
    {
        Assert.Equal(["▸ root"], Render(Tree(), 20, 1));
    }

    [Fact]
    public void Opening_a_node_shows_what_is_under_it()
    {
        var tree = Tree();
        tree.Expand(tree.Root);

        Assert.Equal(["▾ root", "├▸ alpha", "└─ beta"], Render(tree, 20, 3));
    }

    [Fact]
    public void Guides_show_where_a_branch_ends()
    {
        var tree = Tree();
        tree.Reveal([tree.Root, ((dynamic)tree.Root!).Children[0]]);

        Assert.Equal(
            ["▾ root", "├▾ alpha", "│ ├─ one", "│ └─ two", "└─ beta"],
            Render(tree, 20, 5));
    }

    [Fact]
    public void Children_are_asked_for_only_when_a_node_opens()
    {
        // Not a nicety: the CLR explorer's root has every loaded assembly under it, and its
        // leaves are every type in each.
        var asked = new List<string>();

        var tree = new TuiTree(Sample())
        {
            Identify = node => ((Node)node!).Path,
            Display = node => ((Node)node!).Name,
            ChildrenOf = node =>
            {
                asked.Add(((Node)node!).Path);
                return ((Node)node).Children;
            },
        };

        tree.Rebuild();

        // Only the root is visible, and it is asked one question: have you anything under
        // you? Nothing below it is touched.
        Assert.Equal(["/"], asked);

        asked.Clear();
        tree.Expand(tree.Root);

        // Now the root's children are enumerated, and each visible child is asked the same
        // one question — but nothing under *them* is.
        Assert.Equal(["/", "/a", "/b"], asked);
    }

    [Fact]
    public void A_hidden_root_shows_its_children_as_the_top_level()
    {
        Assert.Equal(["▸ alpha", "─ beta"], Render(Tree(showRoot: false), 20, 2));
    }

    [Fact]
    public void Expansion_is_keyed_by_identity_so_it_survives_the_data_changing()
    {
        var tree = Tree();
        tree.Expand(tree.Root);

        // A tree rebuilt from fresh objects — a refreshed filesystem, a re-read config —
        // keeps what the reader had open, because the key is the path and not the instance.
        tree.Root = Sample();
        tree.Rebuild();

        Assert.Equal(3, tree.Rows.Count);
        Assert.True(tree.IsExpanded(tree.Root));
    }

    [Fact]
    public void Right_opens_a_node_and_then_steps_into_it()
    {
        var tree = Focused(Tree());

        tree.OnInput(Key(ConsoleKey.RightArrow));
        Assert.True(tree.IsExpanded(tree.Root));
        Assert.Equal(0, tree.SelectedIndex);

        tree.OnInput(Key(ConsoleKey.RightArrow));
        Assert.Equal(1, tree.SelectedIndex);
    }

    [Fact]
    public void Left_closes_a_node_and_then_steps_out_to_its_parent()
    {
        var tree = Focused(Tree());
        tree.Expand(tree.Root);
        tree.SelectedIndex = 1;

        tree.OnInput(Key(ConsoleKey.RightArrow));
        Assert.Equal("alpha", ((Node)tree.SelectedNode!).Name);

        tree.OnInput(Key(ConsoleKey.LeftArrow));
        Assert.False(tree.IsExpanded(tree.SelectedNode));

        tree.OnInput(Key(ConsoleKey.LeftArrow));
        Assert.Equal("root", ((Node)tree.SelectedNode!).Name);
    }

    [Fact]
    public void Enter_toggles_a_branch_and_activates_a_leaf()
    {
        object? activated = null;
        var tree = Focused(Tree());

        tree.ChildrenOf = node => ((Node)node!).Children;
        tree.Activated = node => activated = node;
        tree.Expand(tree.Root);
        tree.SelectedIndex = 2;

        // `beta` is a leaf, so Enter is an answer rather than an opening.
        tree.OnInput(Key(ConsoleKey.Enter));
        Assert.Same(tree.SelectedNode, activated);

        // `alpha` is a branch, so Enter opens it and nothing is activated.
        activated = null;
        tree.SelectedIndex = 1;
        tree.OnInput(Key(ConsoleKey.Enter));

        Assert.Null(activated);
        Assert.Equal(5, tree.Rows.Count);
    }

    [Fact]
    public void The_selection_follows_the_node_when_rows_appear_above_it()
    {
        var tree = Focused(Tree());
        tree.Expand(tree.Root);
        tree.SelectedIndex = 2;

        var beta = tree.SelectedNode;

        tree.Expand(((dynamic)tree.Root!).Children[0]);

        Assert.Same(beta, tree.SelectedNode);
        Assert.Equal(4, tree.SelectedIndex);
    }

    [Fact]
    public void A_row_that_says_how_to_draw_itself_draws_itself_entirely()
    {
        var tree = Tree();
        tree.LineSelector = (row, selected) => new TuiSpanLine()
            .Add(new string('.', row.Depth))
            .Add(((Node)row.Node!).Name, selected ? new TuiStyle("cyan") : default);

        Assert.Equal(["root"], Render(tree, 20, 1));
    }

    [Fact]
    public void Clicking_a_row_selects_it()
    {
        var tree = Tree();
        tree.Expand(tree.Root);
        tree.Arrange(new TuiRect(0, 0, 20, 5));

        tree.OnInput(TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 2, 2, false, false, false)));

        Assert.Equal("beta", ((Node)tree.SelectedNode!).Name);
    }

    [Fact]
    public void A_host_can_say_what_a_tree_draws_with_when_nobody_else_does()
    {
        // The widgets know nothing about a shell, so the shell installs its theme's answer
        // rather than the tree going looking for one.
        var previous = TuiTree.DefaultGlyphs;

        try
        {
            TuiTree.DefaultGlyphs = () => TuiTreeGlyphs.Clean;

            var tree = Tree();
            tree.Expand(tree.Root);

            // Clean mirrors Default's widths with blanks, so rows line up either way.
            Assert.Equal(["▾ root", " ▸ alpha", "   beta"], Render(tree, 20, 3));

            // A tree that says what it wants still gets it.
            tree.Glyphs = TuiTreeGlyphs.Default;
            Assert.Equal(["▾ root", "├▸ alpha", "└─ beta"], Render(tree, 20, 3));
        }
        finally
        {
            TuiTree.DefaultGlyphs = previous;
        }
    }

    [Fact]
    public void The_glyphs_can_be_swapped_for_a_terminal_that_cannot_draw_boxes()
    {
        var tree = Tree();
        tree.Glyphs = TuiTreeGlyphs.Ascii;
        tree.Expand(tree.Root);

        Assert.Equal(["- root", "|+ alpha", "`  beta"], Render(tree, 20, 3));
    }
}

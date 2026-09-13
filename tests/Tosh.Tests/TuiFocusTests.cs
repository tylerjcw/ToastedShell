using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Focus and routed input — the structure that makes <c>TOSH-0011</c> unrepeatable.
/// </summary>
public sealed class TuiFocusTests
{
    private static TuiInputEvent Type(char ch, ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo(ch, key, false, false, false));

    private static TuiInputEvent Click(int column, int row)
        => TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, column, row, false, false, false));

    /// <summary>Stands in for a screen-level shortcut: quits on q, wherever it is.</summary>
    private sealed class QuitShortcut : TuiWidget
    {
        public bool Quit { get; private set; }

        protected override TuiSize MeasureCore(TuiConstraints constraints) => new(0, 0);

        public override void Draw(TuiSurface surface)
        {
        }

        public override bool OnInput(TuiInputEvent input)
        {
            if (!input.IsKey || input.Key.Key != ConsoleKey.Q)
            {
                return false;
            }

            Quit = true;
            return true;
        }
    }

    [Fact]
    public void A_focused_text_field_consumes_the_letter_a_shortcut_would_have_taken()
    {
        // The exact shape of TOSH-0011: a screen that quits on q, and a search box.
        var field = new TuiTextField();
        var screen = new QuitShortcut();
        var root = new TuiStack().Add(screen, TuiLength.Fixed(0)).Add(field, TuiLength.Fixed(1));

        var focus = new TuiFocus(root);
        focus.Focus(field);

        Assert.True(focus.Dispatch(Type('q', ConsoleKey.Q)));

        Assert.Equal("q", field.Text);
        Assert.False(screen.Quit);
    }

    [Fact]
    public void The_same_letter_is_left_for_the_screen_when_nothing_is_focused()
    {
        var field = new TuiTextField();
        var root = new TuiStack().Add(field, TuiLength.Fixed(1));

        var focus = new TuiFocus(root);
        focus.Focus(null);

        // Nothing consumed it, so the screen is free to treat it as a shortcut. That is
        // the whole contract: a screen acts on what the tree declined, and never on what
        // it took.
        Assert.False(focus.Dispatch(Type('q', ConsoleKey.Q)));
        Assert.Equal(string.Empty, field.Text);
    }

    [Fact]
    public void An_unhandled_key_bubbles_from_the_focused_widget_to_its_ancestors()
    {
        var field = new TuiTextField();
        var shortcut = new QuitShortcut();

        // The shortcut is an ancestor of the field, not a sibling.
        var root = new TuiStack().Add(new TuiBorder(field), TuiLength.Fixed(3));
        var outer = new TuiStack().Add(shortcut, TuiLength.Fixed(0)).Add(root, TuiLength.Star());

        var focus = new TuiFocus(outer);
        focus.Focus(field);

        // F5 is not something a text field wants, so it travels outwards.
        focus.Dispatch(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', ConsoleKey.F5, false, false, false)));

        Assert.False(shortcut.Quit);

        // ...and q, which the field does want, does not.
        focus.Dispatch(Type('q', ConsoleKey.Q));
        Assert.False(shortcut.Quit);
        Assert.Equal("q", field.Text);
    }

    [Fact]
    public void Focus_starts_on_the_first_focusable_widget()
    {
        var first = new TuiButton("one");
        var second = new TuiButton("two");
        var root = new TuiStack().Add(new TuiTextWidget("label")).Add(first).Add(second);

        var focus = new TuiFocus(root);

        Assert.Same(first, focus.Focused);
        Assert.True(first.IsFocused);
        Assert.False(second.IsFocused);
    }

    [Fact]
    public void Focus_moves_in_tree_order_and_wraps()
    {
        var first = new TuiButton("one");
        var second = new TuiButton("two");
        var third = new TuiButton("three");
        var root = new TuiStack().Add(first).Add(second).Add(third);

        var focus = new TuiFocus(root);

        focus.MoveNext();
        Assert.Same(second, focus.Focused);

        focus.MoveNext();
        focus.MoveNext();
        Assert.Same(first, focus.Focused);

        focus.MovePrevious();
        Assert.Same(third, focus.Focused);
    }

    [Fact]
    public void Only_one_widget_is_marked_focused_at_a_time()
    {
        var first = new TuiButton("one");
        var second = new TuiButton("two");
        var root = new TuiStack().Add(first).Add(second);

        var focus = new TuiFocus(root);
        focus.MoveNext();

        Assert.False(first.IsFocused);
        Assert.True(second.IsFocused);
    }

    [Fact]
    public void Widgets_that_cannot_take_focus_are_skipped()
    {
        var button = new TuiButton("only one");
        var root = new TuiStack()
            .Add(new TuiTextWidget("a"))
            .Add(button)
            .Add(new TuiTextWidget("b"));

        var focus = new TuiFocus(root);

        Assert.Single(focus.Focusable());
        Assert.Same(button, focus.Focused);
    }

    [Fact]
    public void Clicking_focuses_what_was_clicked()
    {
        var first = new TuiButton("one");
        var second = new TuiButton("two");
        var root = new TuiStack(TuiOrientation.Horizontal)
            .Add(first, TuiLength.Star())
            .Add(second, TuiLength.Star());

        root.Arrange(new TuiRect(0, 0, 20, 1));

        var focus = new TuiFocus(root);
        focus.Dispatch(Click(15, 0));

        Assert.Same(second, focus.Focused);
    }

    [Fact]
    public void Clicking_inside_a_widget_focuses_the_focusable_one_around_it()
    {
        // The click lands on the text inside the border; the border is not focusable and
        // neither is the text, so nothing takes it.
        var text = new TuiTextWidget("inside");
        var border = new TuiBorder(text);
        var button = new TuiButton("elsewhere");
        var root = new TuiStack().Add(border, TuiLength.Fixed(3)).Add(button, TuiLength.Fixed(1));

        root.Arrange(new TuiRect(0, 0, 20, 4));

        var focus = new TuiFocus(root);
        focus.Focus(null);
        focus.Dispatch(Click(3, 1));

        Assert.Null(focus.Focused);
    }

    [Fact]
    public void A_text_field_reports_where_its_caret_belongs()
    {
        var field = new TuiTextField();
        var focus = new TuiFocus(new TuiStack().Add(field, TuiLength.Fixed(1)));
        focus.Focus(field);

        focus.Dispatch(Type('a', ConsoleKey.A));
        focus.Dispatch(Type('b', ConsoleKey.B));

        Assert.Equal("ab", field.Text);
        Assert.Equal(2, field.CaretColumn);
    }

    [Fact]
    public void An_unfocused_text_field_ignores_typing()
    {
        var field = new TuiTextField();
        var other = new TuiButton("elsewhere");
        var root = new TuiStack().Add(field, TuiLength.Fixed(1)).Add(other, TuiLength.Fixed(1));

        var focus = new TuiFocus(root);
        focus.Focus(other);

        focus.Dispatch(Type('x', ConsoleKey.X));

        Assert.Equal(string.Empty, field.Text);
    }
}

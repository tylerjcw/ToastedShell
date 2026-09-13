using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A dialog is a layer, not a splice into someone else's output.
/// </summary>
/// <remarks>
/// The screens that needed one wrote its lines over the rows they happened to cover, so
/// it could not be clicked, could not be focused, and had to be tested for at the top of
/// every key handler.
/// </remarks>
public sealed class TuiOverlayTests
{
    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static TuiInputEvent Click(int column, int row)
        => TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, column, row, false, false, false));

    [Fact]
    public void With_nothing_up_the_content_is_all_there_is()
    {
        var overlay = new TuiOverlay(new TuiTextWidget("beneath"));

        Assert.Equal(["beneath", "", ""], Render(overlay, 9, 3));
        Assert.False(overlay.IsModal);
    }

    [Fact]
    public void A_modal_draws_over_the_content_at_its_own_size()
    {
        var overlay = new TuiOverlay(
            new TuiTextWidget("aaaaaaaaa\naaaaaaaaa\naaaaaaaaa\naaaaaaaaa\naaaaaaaaa") { Wrap = false },
            new TuiTextWidget("hi"))
        {
            Margin = 1,
        };

        var rows = Render(overlay, 9, 5);

        // Centred, and only as big as it asked to be — the rest of the content still shows.
        Assert.Equal("aaaaaaaaa", rows[0]);
        Assert.Contains("hi", rows[2], StringComparison.Ordinal);
        Assert.Equal("aaaaaaaaa", rows[4]);
    }

    [Fact]
    public void The_rows_a_modal_covers_are_cleared_rather_than_shown_through()
    {
        var overlay = new TuiOverlay(
            new TuiTextWidget(string.Join('\n', Enumerable.Repeat("xxxxxxxxxx", 5))) { Wrap = false },
            new TuiTextWidget("ok\n") { Wrap = false })
        {
            Margin = 1,
        };

        var rows = Render(overlay, 10, 5);

        // The modal measures two rows and paints one. The row it owns but does not paint
        // is blank, not the content still showing through it.
        Assert.Equal("xxxxokxxxx", rows[1]);
        Assert.Equal("xxxx  xxxx", rows[2]);
    }

    [Fact]
    public void While_a_modal_is_up_the_keyboard_cannot_reach_what_is_behind_it()
    {
        var beneath = new TuiTextField("behind");
        var inside = new TuiTextField("inside");
        var overlay = new TuiOverlay(beneath);

        var focus = new TuiFocus(overlay);
        Assert.Same(beneath, focus.Focused);

        overlay.Modal = inside;

        var scoped = new TuiFocus(overlay);
        Assert.Same(inside, scoped.Focused);
        Assert.Equal([inside], scoped.Focusable());

        // Tab has nowhere else to go, so it stays put rather than stepping behind.
        scoped.MoveNext();
        Assert.Same(inside, scoped.Focused);
    }

    [Fact]
    public void Dismissing_a_modal_gives_the_keyboard_back()
    {
        var beneath = new TuiTextField("behind");
        var overlay = new TuiOverlay(beneath, new TuiTextField("inside"));

        overlay.Modal = null;

        Assert.Same(beneath, new TuiFocus(overlay).Focused);
    }

    [Fact]
    public void A_click_outside_a_modal_does_not_reach_the_content()
    {
        var beneath = new TuiList(["a", "b", "c"]);
        var overlay = new TuiOverlay(beneath, new TuiTextWidget("dialog")) { Margin = 1 };

        overlay.Measure(TuiConstraints.From(new TuiSize(20, 5)));
        overlay.Arrange(new TuiRect(0, 0, 20, 5));

        var focus = new TuiFocus(overlay);
        focus.Dispatch(Click(1, 0));

        // Row zero is the list's third item only if the click got through. It must not.
        Assert.Equal(0, beneath.SelectedIndex);
        Assert.Same(overlay, overlay.HitTest(1, 0));
    }

    [Fact]
    public void A_click_inside_a_modal_reaches_it()
    {
        var inside = new TuiList(["x", "y", "z"]);
        var overlay = new TuiOverlay(new TuiTextWidget("beneath"), inside) { Margin = 0 };

        overlay.Measure(TuiConstraints.From(new TuiSize(20, 3)));
        overlay.Arrange(new TuiRect(0, 0, 20, 3));

        // The modal is only as wide as it asked to be and sits centred, so a click aimed
        // at it has to be aimed at where it actually is.
        new TuiFocus(overlay).Dispatch(Click(inside.Bounds.Left + 1, inside.Bounds.Top + 2));

        Assert.Equal(2, inside.SelectedIndex);
    }

    [Fact]
    public void A_dialog_a_handler_puts_up_takes_the_next_key()
    {
        // The keyboard was seated when the tree was built, before there was a dialog. If
        // nothing re-seats it, Enter goes to the form the reader can no longer see while
        // the dialog in front of them does nothing.
        var form = new TuiForm(new TuiTextField("draft"));
        var discard = new TuiButton("Discard") { IsSelected = true };
        var overlay = new TuiOverlay(form);

        var discarded = false;
        discard.Pressed = () => discarded = true;

        form.Cancelled = closing =>
        {
            overlay.Modal = discard;
            closing.KeepOpen();
        };

        var screen = new Tosh.Tui.Declarative.TuiDeclarativeScreen(overlay, []);
        screen.Render(new TuiSize(30, 8));

        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Key(ConsoleKey.Escape)));
        Assert.True(overlay.IsModal);
        Assert.Equal(TuiFormResult.Open, form.Result);

        screen.Render(new TuiSize(30, 8));
        screen.HandleInput(Key(ConsoleKey.Enter));

        Assert.True(discarded);
    }

    [Fact]
    public void Once_the_dialog_is_gone_the_form_answers_again()
    {
        var form = new TuiForm(new TuiTextField("draft"));
        var overlay = new TuiOverlay(form);
        var dismiss = new TuiButton("OK") { IsSelected = true };

        dismiss.Pressed = () => overlay.Modal = null;
        overlay.Modal = dismiss;

        var screen = new Tosh.Tui.Declarative.TuiDeclarativeScreen(overlay, []);
        screen.Render(new TuiSize(30, 8));

        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Key(ConsoleKey.Enter)));
        Assert.False(overlay.IsModal);

        screen.Render(new TuiSize(30, 8));

        Assert.Equal(TuiScreenResult.Exit, screen.HandleInput(Key(ConsoleKey.Enter)));
        Assert.True(form.WasSubmitted);
    }

    [Fact]
    public void A_form_behind_a_dialog_is_not_submitted_by_the_dialog_s_enter()
    {
        // The shape this exists for: a confirmation over a form. Enter belongs to whatever
        // is on top, and only reaches the form once the dialog has gone.
        var form = new TuiForm(new TuiTextField("draft"));
        var dialog = new TuiButton("Discard") { IsSelected = true };
        var overlay = new TuiOverlay(form, dialog);

        var discarded = false;
        dialog.Pressed = () => discarded = true;

        var screen = new Tosh.Tui.Declarative.TuiDeclarativeScreen(overlay, []);
        screen.Render(new TuiSize(30, 8));
        screen.HandleInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', ConsoleKey.Enter, false, false, false)));

        Assert.True(discarded);
        Assert.Equal(TuiFormResult.Open, form.Result);
    }
}

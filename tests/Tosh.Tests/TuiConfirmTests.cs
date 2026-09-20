using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A confirmation dialog that draws itself — <c>TUI-0002</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TuiConfirmationDialogState"/> reads the keys and tracks the answer and does
/// not draw, so drawing belonged to whichever screen owned one. <see cref="TuiConfirm"/> is
/// the drawing half and <em>wraps</em> that state rather than restating it, so the keys here
/// are the ones the config browser already answered to.
/// </para>
/// <para>
/// The point of it being a widget is that a script can raise one without knowing how a
/// dialog is laid out.
/// </para>
/// </remarks>
public sealed class TuiConfirmTests
{
    private const int Width = 40;
    private const int Height = 12;

    private static TuiConfirm Laid(TuiConfirm confirm)
    {
        confirm.Measure(new TuiConstraints(Width, Height));
        confirm.Arrange(new TuiRect(0, 0, Width, Height));

        return confirm;
    }

    private static string Render(TuiConfirm confirm)
    {
        var buffer = new TuiBuffer(new TuiSize(Width, Height));

        Laid(confirm).Draw(new TuiSurface(buffer, new TuiRect(0, 0, Width, Height)));

        return string.Join('\n', Enumerable.Range(0, Height).Select(row => buffer.RowText(row).TrimEnd()));
    }

    private static bool Press(TuiConfirm confirm, ConsoleKey key)
        => confirm.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false)));

    [Fact]
    public void It_draws_the_question_and_both_answers()
    {
        var text = Render(new TuiConfirm("Discard your changes?", "Confirm exit"));

        Assert.Contains("Discard your changes?", text, StringComparison.Ordinal);
        Assert.Contains("Confirm", text, StringComparison.Ordinal);
        Assert.Contains("Cancel", text, StringComparison.Ordinal);
        Assert.Contains("Confirm exit", text, StringComparison.Ordinal);
    }

    /// <summary>The wording is the caller's; a dialog is not always about saving.</summary>
    [Fact]
    public void The_answers_can_be_worded_by_the_caller()
    {
        var text = Render(new TuiConfirm("Remove it?") { ConfirmLabel = "Remove", CancelLabel = "Keep" });

        Assert.Contains("Remove", text, StringComparison.Ordinal);
        Assert.Contains("Keep", text, StringComparison.Ordinal);
    }

    /// <summary>The selection is visible, or the reader cannot tell what Enter would do.</summary>
    [Fact]
    public void The_selected_answer_is_marked()
    {
        var confirm = new TuiConfirm("Sure?");

        var whenConfirm = Render(confirm);
        confirm.ConfirmSelected = false;
        var whenCancel = Render(confirm);

        Assert.NotEqual(whenConfirm, whenCancel);
        Assert.Contains("> [Confirm]", whenConfirm, StringComparison.Ordinal);
        Assert.Contains("> [Cancel]", whenCancel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConsoleKey.LeftArrow)]
    [InlineData(ConsoleKey.RightArrow)]
    [InlineData(ConsoleKey.Tab)]
    public void The_arrows_and_tab_move_the_selection(ConsoleKey key)
    {
        var confirm = Laid(new TuiConfirm("Sure?"));

        Assert.True(confirm.ConfirmSelected);
        Assert.True(Press(confirm, key));
        Assert.False(confirm.ConfirmSelected);
    }

    /// <summary>
    /// Handled even though nothing was answered: the arrows belong to the dialog while it is
    /// up, and letting them past would move whatever is behind it.
    /// </summary>
    [Fact]
    public void A_selection_change_does_not_answer()
    {
        bool? answer = null;
        var confirm = Laid(new TuiConfirm("Sure?") { Answered = value => answer = value });

        Press(confirm, ConsoleKey.LeftArrow);

        Assert.Null(answer);
    }

    [Theory]
    [InlineData(ConsoleKey.Y, true)]
    [InlineData(ConsoleKey.N, false)]
    [InlineData(ConsoleKey.Escape, false)]
    public void A_letter_or_escape_answers_outright(ConsoleKey key, bool expected)
    {
        bool? answer = null;
        var confirm = Laid(new TuiConfirm("Sure?") { Answered = value => answer = value });

        Assert.True(Press(confirm, key));
        Assert.Equal(expected, answer);
    }

    /// <summary>Enter answers with whatever is selected, which is the other half of the arrows.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Enter_answers_with_the_selection(bool confirmSelected, bool expected)
    {
        bool? answer = null;
        var confirm = Laid(new TuiConfirm("Sure?")
        {
            ConfirmSelected = confirmSelected,
            Answered = value => answer = value,
        });

        Assert.True(Press(confirm, ConsoleKey.Enter));
        Assert.Equal(expected, answer);
    }

    /// <summary>A key the dialog has no use for is left for whatever else wants it.</summary>
    [Fact]
    public void An_unrelated_key_is_not_swallowed()
        => Assert.False(Press(Laid(new TuiConfirm("Sure?")), ConsoleKey.F5));

    /// <summary>
    /// A long question wraps rather than running off the edge. Sized to the message, not to
    /// the screen: a confirmation that fills the window is a screen.
    /// </summary>
    [Fact]
    public void A_long_question_wraps_inside_the_frame()
    {
        var text = Render(new TuiConfirm(string.Join(' ', Enumerable.Repeat("word", 30))));

        foreach (var line in text.Split('\n'))
        {
            Assert.True(
                TextMeasure.MeasureWidth(line) <= Width,
                $"line ran past the frame: '{line}'");
        }
    }
}

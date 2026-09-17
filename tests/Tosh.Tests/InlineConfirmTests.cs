using Tosh.Cli;
using Tosh.Tui;

namespace Tosh.Tests;

/// <summary>
/// What <c>tui confirm --cli</c> actually draws, and when.
/// </summary>
/// <remarks>
/// Every other test of the inline prompts uses a fake provider, so what the real one puts
/// on the console was untested — which is how it came to leave an unclosed box on screen
/// for as long as it waited for an answer.
/// </remarks>
public sealed class InlineConfirmTests
{
    /// <summary>Runs a confirm with the keys already queued, and returns what it wrote.</summary>
    private static (bool? Answer, string Written) Answer(params ConsoleKey[] keys)
    {
        var provider = new ConsoleInlinePromptProvider();

        foreach (var key in keys)
        {
            provider.InputReader.Enqueue(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false)));
        }

        var captured = new StringWriter();
        var previous = Console.Out;

        Console.SetOut(captured);

        try
        {
            return (provider.Confirm("Deploy now?"), captured.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    /// <summary>Where a bottom-left corner first appears in what was written.</summary>
    private static int FirstBottom(string written)
        => written.IndexOfAny(['╰', '└', '╚']);

    [Fact]
    public void The_box_is_closed_before_the_answer_is_asked_for()
    {
        // The bug: the bottom border was written only once a key had arrived, so the box
        // sat open for as long as the prompt waited — which is most of the time a prompt
        // exists. Order is what says so: a closing corner must reach the console before
        // the answer does.
        var (_, written) = Answer(ConsoleKey.Y);

        var closed = FirstBottom(written);
        var answered = written.IndexOf("yes", StringComparison.Ordinal);

        Assert.True(closed >= 0, "The box was never closed.");
        Assert.True(answered >= 0, "The answer was never drawn.");
        Assert.True(closed < answered, "The box was only closed after the answer arrived.");
    }

    [Theory]
    [InlineData(ConsoleKey.Y, true, "yes")]
    [InlineData(ConsoleKey.N, false, "no")]
    [InlineData(ConsoleKey.Enter, true, "yes")]
    public void An_answer_is_read_and_shown(ConsoleKey key, bool expected, string shown)
    {
        var (answer, written) = Answer(key);

        Assert.Equal(expected, answer);
        Assert.Contains(shown, written, StringComparison.Ordinal);
    }

    [Fact]
    public void Escape_cancels_and_says_so()
    {
        var (answer, written) = Answer(ConsoleKey.Escape);

        Assert.Null(answer);
        Assert.Contains("cancelled", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_means_nothing_is_ignored_rather_than_taken_as_no()
    {
        var (answer, _) = Answer(ConsoleKey.Spacebar, ConsoleKey.Tab, ConsoleKey.N);

        Assert.False(answer);
    }

    [Fact]
    public void The_answer_is_drawn_over_the_question_rather_than_under_it()
    {
        // The box is three rows and stays three rows. The question is written twice — once
        // waiting and once answered — so what makes the second one an overwrite rather than
        // an append is the reposition before it: back up onto the content row, and erase
        // the line before redrawing it.
        var (_, written) = Answer(ConsoleKey.Y);

        var up = written.IndexOf("\x1b[1A", StringComparison.Ordinal);
        var answered = written.IndexOf("yes", StringComparison.Ordinal);

        Assert.True(up >= 0, "The prompt never moved back onto the content row.");
        Assert.True(up < answered, "The answer was drawn before the cursor was moved back.");
        Assert.Contains("\x1b[2K", written, StringComparison.Ordinal);
    }
}

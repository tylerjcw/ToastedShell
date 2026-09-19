using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Telling the terminal a frame is arriving in pieces, and when it is whole.
/// </summary>
/// <remarks>
/// <para>
/// A frame reaches the terminal as a stream of cursor moves and runs of text, and the
/// terminal is free to draw as it reads. A reader therefore sees frames half-applied — the
/// top of a table from this frame above the bottom of the last — which on a screen
/// refreshing once a second is a visible tear every tick.
/// </para>
/// <para>
/// Mode 2026 asks the terminal to hold what it has until the update ends. Nothing about the
/// frame changes; it stops being seen in the middle of arriving.
/// </para>
/// </remarks>
public sealed class TuiSynchronizedOutputTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>A frame is bracketed by the begin and end sequences, in that order.</summary>
    [Fact]
    public void A_frame_is_bracketed()
    {
        var wrapped = TuiSynchronizedOutput.Wrap("\x1b[1;1Hhello");

        Assert.StartsWith(TuiSynchronizedOutput.Begin, wrapped, StringComparison.Ordinal);
        Assert.EndsWith(TuiSynchronizedOutput.End, wrapped, StringComparison.Ordinal);
        Assert.Contains("hello", wrapped, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty frame stays empty.
    /// </summary>
    /// <remarks>
    /// A diff that found nothing is most of the frames a waiting screen produces. Bracketing
    /// nothing would put two escapes on the wire every tick to announce that nothing
    /// happened.
    /// </remarks>
    [Fact]
    public void An_empty_frame_is_left_alone()
        => Assert.Equal(string.Empty, TuiSynchronizedOutput.Wrap(string.Empty));

    /// <summary>The sequences are the ones mode 2026 is spelled with.</summary>
    /// <remarks>
    /// Pinned because a typo here is invisible: a terminal ignores a private mode it does
    /// not recognise, so a wrong number looks exactly like a terminal without support.
    /// </remarks>
    [Fact]
    public void The_sequences_are_mode_2026()
    {
        Assert.Equal("\x1b[?2026h", TuiSynchronizedOutput.Begin);
        Assert.Equal("\x1b[?2026l", TuiSynchronizedOutput.End);
    }

    /// <summary>
    /// On by default, because a terminal without support is unharmed.
    /// </summary>
    /// <remarks>
    /// An opt-out rather than an opt-in: the default that helps most readers is the one that
    /// does not need setting.
    /// </remarks>
    [Fact]
    public void It_is_on_when_nothing_is_said()
        => Assert.True(TuiSynchronizedOutput.Detect(Env()));

    /// <summary>The opt-out is for a terminal that claims the mode and mishandles it.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("no")]
    public void It_can_be_turned_off(string value)
        => Assert.False(TuiSynchronizedOutput.Detect(Env(("TOSH_TUI_SYNC", value))));

    /// <summary>Anything else means yes, including the obvious spellings of it.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("true")]
    [InlineData("")]
    public void Anything_else_leaves_it_on(string value)
        => Assert.True(TuiSynchronizedOutput.Detect(Env(("TOSH_TUI_SYNC", value))));
}

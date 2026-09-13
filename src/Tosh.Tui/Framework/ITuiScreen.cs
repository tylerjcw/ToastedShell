namespace Tosh.Tui;

/// <summary>A screen the TUI runtime can draw and drive.</summary>
/// <remarks>
/// The pair is deliberately pure: <see cref="Render"/> projects state to a frame and
/// <see cref="HandleInput"/> folds an event into state. Neither touches a terminal, which
/// is what lets a screen be driven headlessly in tests.
/// </remarks>
public interface ITuiScreen
{
    /// <summary>Draws the screen at the given terminal size.</summary>
    TuiFrame Render(TuiSize size);

    /// <summary>Folds one input event into the screen's state.</summary>
    TuiScreenResult HandleInput(TuiInputEvent input);

    /// <summary>
    /// How often this screen wants redrawing when no input arrives, or <see langword="null"/>
    /// to redraw only in response to input.
    /// </summary>
    /// <remarks>
    /// Most screens show data that only changes when the user does something, and for those
    /// the runtime should block on the keyboard rather than spin. A screen that watches
    /// something outside itself — processes, a log, a download — sets an interval and gets
    /// <see cref="Tick"/> called when it elapses.
    /// </remarks>
    TimeSpan? RefreshInterval => null;

    /// <summary>
    /// Called when <see cref="RefreshInterval"/> elapses with no input, before the redraw.
    /// </summary>
    /// <remarks>
    /// This is where a live screen re-samples whatever it is watching. Sampling belongs here
    /// rather than in <see cref="Render"/> so that rendering stays a projection of state and
    /// can still be replayed frame by frame in a test.
    /// </remarks>
    TuiScreenResult Tick() => TuiScreenResult.Continue;

    /// <summary>
    /// The way into this screen from another thread, or <see langword="null"/> if nothing
    /// outside the loop can change it.
    /// </summary>
    /// <remarks>
    /// A screen with sources hands one over and the loop waits on it as well as on the
    /// keyboard, so a value arriving draws a frame instead of waiting for an interval that
    /// may not exist (<c>TUI-0008</c>). A screen without one behaves exactly as before: the
    /// loop blocks on the keyboard and spins for nobody.
    /// </remarks>
    TuiWake? Wake => null;
}

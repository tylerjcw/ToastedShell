using Tosh.Tui.Rendering;

namespace Tosh.Tui;

/// <summary>
/// Catches what a screen's handlers throw, shows it, and keeps the screen running.
/// </summary>
/// <remarks>
/// <para>
/// Screens now call back into user code — a tick handler re-samples whatever a live
/// screen is watching, and that handler is an ordinary script function. An ordinary
/// script mistake in one used to propagate out of the render loop and end the session
/// (<c>TUI-0010</c>). A misspelled property should not close the window.
/// </para>
/// <para>
/// The same failure usually recurs on every tick, so it is reported once and then
/// counted rather than redrawn as new each second.
/// </para>
/// </remarks>
internal sealed class TuiHandlerFailureReporter
{
    private static readonly TuiStyle BannerStyle =
        new(Foreground: "brightwhite", Background: "red", Attributes: TuiTextAttributes.Bold);

    private string? _message;
    private int _count;

    /// <summary>
    /// Runs a handler, converting a failure into something drawn rather than thrown.
    /// </summary>
    /// <remarks>
    /// The screen continues on the assumption that its state is still usable. That holds
    /// for the failure this exists to catch — a handler that threw part way through
    /// updating a widget — and would not for a corrupt screen, which is a bug the screen
    /// has to be fixed for rather than one the loop can paper over.
    /// </remarks>
    public TuiScreenResult Guard(Func<TuiScreenResult> handler)
    {
        try
        {
            return handler();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                              and not StackOverflowException)
        {
            Record(exception);
            return TuiScreenResult.Continue;
        }
    }

    private void Record(Exception exception)
    {
        var message = $"{exception.GetType().Name}: {exception.Message}";

        if (message == _message)
        {
            _count += 1;
            return;
        }

        _message = message;
        _count = 1;
    }

    /// <summary>The banner text, or null when nothing has failed.</summary>
    private string? BannerText()
    {
        if (_message is null)
        {
            return null;
        }

        // The count goes before the message, not after it: the message is the part
        // long enough to be clipped by a narrow terminal, and "this has happened 40
        // times" is the more useful half to keep.
        var repeats = _count > 1 ? $" (x{_count})" : string.Empty;
        return $" handler failed{repeats} — {_message} ";
    }

    /// <summary>Draws the banner across the bottom row of a frame.</summary>
    public void Draw(TuiBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (BannerText() is not { } text || buffer.Height == 0)
        {
            return;
        }

        var row = buffer.Height - 1;
        buffer.Fill(new TuiRect(0, row, buffer.Width, 1), BannerStyle);
        buffer.DrawText(0, row, text, BannerStyle);
    }
}

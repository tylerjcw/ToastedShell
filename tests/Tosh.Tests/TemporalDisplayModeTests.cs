using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What the `Local` display mode does with a `DateTime`'s kind — `TOSH-0006`.
///
/// The mode called `value.ToLocalTime()`, and .NET's `ToLocalTime` **assumes an
/// `Unspecified` kind means UTC** and converts from it. So a value written `12:00:00`
/// displayed as `08:00:00` on a UTC−4 machine, with no timezone ever stated.
///
/// The file already held the right answer: `ToDisplayInstant` reads `Unspecified` as
/// *local* — what a wall-clock literal means — and the `Relative` and `Unix` modes were
/// already going through it. One mode disagreed with the other three about the same
/// question.
///
/// The display twin of `TOAST-0017`, which fixed this for rendering. Display and rendering
/// may differ in *format*; they must not differ about which instant they describe.
/// </summary>
public sealed class TemporalDisplayModeTests
{
    private static string Format(DateTime value)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Display.DateTime.ScalarMode = TemporalDisplayMode.Local;

        return StyledText.StripAnsi(runtime.Formatter.Format(value));
    }

    /// <summary>
    /// No timezone was stated, so none is applied.
    /// </summary>
    [Fact]
    public void An_unspecified_datetime_displays_its_own_clock_reading()
        => Assert.Equal("2026-08-17 12:00:00", Format(new DateTime(2026, 8, 17, 12, 0, 0)));

    /// <summary>
    /// A UTC value still converts, which is the entire point of the mode. Without this the
    /// fix could have been "never convert", which would break what the mode is for.
    /// </summary>
    [Fact]
    public void A_utc_datetime_still_converts_to_local_time()
    {
        var noon = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Local);
        var utc = noon.ToUniversalTime();

        Assert.Equal(noon.ToString("yyyy-MM-dd HH:mm:ss"), Format(utc));
    }

    /// <summary>
    /// A value already marked local is unchanged.
    /// </summary>
    [Fact]
    public void A_local_datetime_is_unchanged()
        => Assert.Equal(
            "2026-08-17 12:00:00",
            Format(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Local)));

    /// <summary>
    /// Display and rendering describe the same instant. They may format it differently —
    /// that is what a profile is for — but a value written `12:00` is `12:00` in both.
    /// </summary>
    [Fact]
    public void Display_and_rendering_agree_about_the_instant()
    {
        var value = new DateTime(2026, 8, 17, 12, 0, 0);

        Assert.Equal(ToastRenderer.Render(value), Format(value));
    }
}

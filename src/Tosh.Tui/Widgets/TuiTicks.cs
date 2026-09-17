using System.Globalization;

namespace Tosh.Tui.Widgets;

/// <summary>A scale a person would have chosen, and the values marked along it.</summary>
/// <param name="Minimum">The bottom of the axis, at or below the smallest value.</param>
/// <param name="Maximum">The top of the axis, at or above the largest.</param>
/// <param name="Step">The distance between marks.</param>
public readonly record struct TuiScale(double Minimum, double Maximum, double Step)
{
    /// <summary>The marked values, from the bottom up.</summary>
    public IEnumerable<double> Values
    {
        get
        {
            if (Step <= 0)
            {
                yield return Minimum;
                yield break;
            }

            // Counted rather than accumulated: adding 0.1 ten times is 0.9999999999999999,
            // and an axis labelled 0.7000000000000001 is an axis nobody trusts.
            var count = (int)Math.Round((Maximum - Minimum) / Step);

            for (var index = 0; index <= count; index += 1)
            {
                yield return Minimum + (index * Step);
            }
        }
    }

    /// <summary>Where a value sits on this scale, from 0 at the bottom to 1 at the top.</summary>
    public double Fraction(double value)
        => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);

    /// <summary>
    /// A label with only the digits this step needs.
    /// </summary>
    /// <remarks>
    /// The step decides the precision, not the value: an axis stepping by 0.5 wants
    /// <c>1.5</c> and <c>2.0</c>, and one stepping by 10 wants <c>20</c> rather than
    /// <c>20.0</c>. Formatting each label from its own value gives a column of labels with
    /// different widths, which is how an axis comes out ragged.
    /// </remarks>
    public string Format(double value)
    {
        var decimals = 0;

        // How many places the step itself needs, rather than a logarithm of it: a step of
        // 0.25 has a log that says one place and needs two, which rounds 0.25 to 0.3 and
        // then prints two ticks running 0.3 and 0.5.
        for (var scaled = Step; decimals < 6 && Math.Abs(scaled - Math.Round(scaled)) > 1e-9; decimals += 1)
        {
            scaled *= 10;
        }

        return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Picks the numbers to mark an axis with.
/// </summary>
/// <remarks>
/// <para>
/// The "nice numbers" idea, which is what every plotting library uses and what a reader's
/// eye expects: a step is a small round multiple of a power of ten, and the ends of the axis
/// are rounded outwards to multiples of it. Dividing a range into equal parts gives
/// 0, 33.3, 66.6 — arithmetically fine and unreadable.
/// </para>
/// <para>
/// Heckbert's version in <em>Graphics Gems</em> computes the spacing and then rounds it,
/// which rounds the wrong way about half the time: 100 over four intervals is exactly 25,
/// and rounding that to the nearest of 1, 2, 5 gives 20 — a step that no longer fits in four
/// intervals. Searching upward for the smallest candidate that <em>does</em> fit cannot make
/// that mistake, and costs a loop over a handful of numbers.
/// </para>
/// </remarks>
public static class TuiTicks
{
    /// <summary>
    /// The candidate steps, as multiples of a power of ten.
    /// </summary>
    /// <remarks>
    /// <c>2.5</c> is in the set and is not in Heckbert's. Without it a 0-to-100 axis steps
    /// by 20, which is readable but is not what anybody draws for a percentage — and a
    /// percentage is the single most common thing a shell script charts. matplotlib made
    /// the same addition for the same reason.
    /// </remarks>
    private static readonly double[] Steps = [1, 2, 2.5, 5];

    /// <summary>A scale covering <paramref name="minimum"/> to <paramref name="maximum"/>.</summary>
    /// <param name="wanted">
    /// Roughly how many marks there is room for. Roughly, because the step has to be a
    /// number a reader recognises, and readability wins over the requested count.
    /// </param>
    /// <remarks>
    /// The smallest nice step that fits, rather than the range divided and then rounded.
    /// Rounding a computed spacing is the usual way to write this and it rounds the wrong
    /// way about half the time: 100 over four intervals is 25, which rounds down to 20 and
    /// then needs five intervals to cover the range.
    /// </remarks>
    public static TuiScale Choose(double minimum, double maximum, int wanted)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            return new TuiScale(0, 1, 1);
        }

        var intervals = Math.Max(1, wanted - 1);

        if (maximum <= minimum)
        {
            // A flat series still needs an axis. One step either side of the value puts it
            // in the middle of the plot rather than pinned to an edge.
            var only = Magnitude(Math.Max(Math.Abs(minimum), 1) / intervals);

            return new TuiScale(minimum - only, minimum + only, only);
        }

        // Start below the smallest step that could possibly work and walk up. Bounded
        // rather than open, because a range that is not finite has already been handled and
        // anything else terminates within a decade or two.
        var exponent = Math.Floor(Math.Log10((maximum - minimum) / intervals)) - 1;

        for (var decade = 0; decade < 24; decade += 1, exponent += 1)
        {
            foreach (var candidate in Steps)
            {
                var step = candidate * Math.Pow(10, exponent);

                if (step <= 0)
                {
                    continue;
                }

                var low = Math.Floor(minimum / step) * step;
                var high = Math.Ceiling(maximum / step) * step;

                if ((high - low) / step <= intervals + 1e-9)
                {
                    return new TuiScale(low, high, step);
                }
            }
        }

        return new TuiScale(minimum, maximum, (maximum - minimum) / intervals);
    }

    /// <summary>The nearest candidate step to a value, for the degenerate cases.</summary>
    private static double Magnitude(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(value));
        var fraction = value / Math.Pow(10, exponent);

        var nice = fraction switch
        {
            < 1.5 => 1,
            < 2.25 => 2,
            < 3.5 => 2.5,
            < 7.5 => 5,
            _ => 10,
        };

        return nice * Math.Pow(10, exponent);
    }
}

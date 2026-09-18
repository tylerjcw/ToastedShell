namespace Tosh.Tui.Widgets;

/// <summary>
/// Divides a line of cells between things that each have an opinion about their size.
/// </summary>
/// <remarks>
/// <para>
/// A stack's children and a grid's rows and columns are the same problem: some want a fixed
/// number of cells, some a share of what is left, some as much as their contents need, and
/// there is rarely exactly enough. This is that arithmetic, once.
/// </para>
/// <para>
/// It was <c>TuiStack.Distribute</c>, and it earned its comments: hidden children paying a
/// share they could not use, the rounding remainder going to a child pinned by a bound, a
/// declared minimum being shared away on a narrow terminal. Writing it a second time for a
/// grid would have been the third copy of the block ramp all over again (<c>TUI-0002</c>).
/// </para>
/// </remarks>
public static class TuiTracks
{
    /// <summary>How many cells each track gets.</summary>
    /// <param name="lengths">What each track asks for.</param>
    /// <param name="visible">
    /// Whether each track counts at all. A hidden one is not a track with nothing in it: it
    /// takes no cells and no share, so its neighbours close up rather than leaving a gap.
    /// </param>
    /// <param name="available">Cells to divide, gaps already taken out.</param>
    /// <param name="natural">
    /// How big track <c>i</c> would like to be, asked only for the ones that say
    /// <see cref="TuiLengthKind.Auto"/>. Measuring is the expensive part, so it is asked for
    /// rather than passed in.
    /// </param>
    public static int[] Allocate(
        IReadOnlyList<TuiLength> lengths,
        IReadOnlyList<bool> visible,
        int available,
        Func<int, int> natural)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(natural);

        var sizes = new int[lengths.Count];
        var wanted = new int[lengths.Count];
        var floors = new int[lengths.Count];
        var claimed = 0;
        var totalWeight = 0;

        // What each track that has an answer of its own asks for, each measured against the
        // whole line rather than against what earlier siblings happened to leave: how big a
        // thing naturally is should not depend on what was written before it.
        for (var index = 0; index < lengths.Count; index += 1)
        {
            if (index >= visible.Count || !visible[index])
            {
                continue;
            }

            var length = lengths[index];

            switch (length.Kind)
            {
                case TuiLengthKind.Fixed:
                    wanted[index] = length.Clamp(length.Value);
                    break;

                case TuiLengthKind.Fraction:
                    wanted[index] = length.Clamp(available * length.Value / length.Divisor);
                    break;

                case TuiLengthKind.Auto:
                    wanted[index] = length.Clamp(natural(index));
                    break;

                case TuiLengthKind.Star:
                    totalWeight += length.Value;
                    continue;
            }

            floors[index] = Math.Min(length.Minimum ?? 0, wanted[index]);
            claimed += wanted[index];
        }

        var remaining = available - Fit(sizes, wanted, floors, claimed, available);

        if (totalWeight == 0)
        {
            return sizes;
        }

        var share = Math.Max(0, remaining);
        var handedOut = 0;
        var lastUnbounded = -1;

        for (var index = 0; index < lengths.Count; index += 1)
        {
            var length = lengths[index];

            // Hidden tracks were left out of the weight, so they must be left out of the
            // sharing too. Paying them anyway ran the total handed out past the share, and
            // the absorber below then subtracted the overspend from the last visible one —
            // which is why a dialog that was the last of its siblings vanished.
            if (length.Kind != TuiLengthKind.Star || index >= visible.Count || !visible[index])
            {
                continue;
            }

            sizes[index] = length.Clamp(share * length.Value / totalWeight);
            handedOut += sizes[index];

            // The rounding error goes to a star track that can absorb it. One pinned by a
            // bound cannot, and giving it the remainder anyway is how a bounded pane ends up
            // one cell wider than it asked to be.
            if (length is { Minimum: null, Maximum: null })
            {
                lastUnbounded = index;
            }
        }

        if (lastUnbounded >= 0)
        {
            sizes[lastUnbounded] = Math.Max(0, sizes[lastUnbounded] + share - handedOut);
        }

        return sizes;
    }

    /// <summary>
    /// Hands out what each track asked for, or, when that is more than there is, the same
    /// proportion of it to each.
    /// </summary>
    /// <returns>How much was handed out in total.</returns>
    /// <remarks>
    /// A declared minimum is protected before anything is shared, so "this column is at
    /// least twelve wide" survives a narrow terminal. What is above the minimums is what
    /// shrinks.
    /// </remarks>
    private static int Fit(int[] sizes, int[] wanted, int[] floors, int claimed, int available)
    {
        if (claimed <= available)
        {
            Array.Copy(wanted, sizes, wanted.Length);
            return claimed;
        }

        var protectedTotal = floors.Sum();
        var room = Math.Max(0, available - protectedTotal);
        var flexible = claimed - protectedTotal;
        var given = 0;

        for (var index = 0; index < sizes.Length; index += 1)
        {
            sizes[index] = flexible <= 0
                ? floors[index]
                : floors[index] + ((wanted[index] - floors[index]) * room / flexible);

            given += sizes[index];
        }

        // Everything asked for more than there is, and the minimums alone are already too
        // much. Nothing fair is left to do, so the ones written first are the ones drawn.
        if (given <= available)
        {
            return given;
        }

        var over = given - available;

        for (var index = sizes.Length - 1; index >= 0 && over > 0; index -= 1)
        {
            var taken = Math.Min(over, sizes[index]);

            sizes[index] -= taken;
            over -= taken;
        }

        return available;
    }
}

namespace Tosh.Tui.Rendering;

/// <summary>One terminal cell: a single visible character and how it is drawn.</summary>
/// <remarks>
/// <para>
/// <see cref="Text"/> holds a whole grapheme cluster, not a <see cref="char"/>. A cell
/// may therefore contain several code units — an emoji, or a letter with a combining
/// accent — because those are one character to the person reading them.
/// </para>
/// <para>
/// A two-column character occupies two cells: the first carries the text and the second
/// is a <em>continuation</em>, holding nothing. Continuations exist so that the grid
/// stays rectangular and so that anything overwriting the right half of a wide character
/// can see that it must clear the left half too.
/// </para>
/// </remarks>
public readonly record struct TuiCell(string Text, TuiStyle Style)
{
    /// <summary>A blank cell.</summary>
    public static readonly TuiCell Empty = new(" ", TuiStyle.Default);

    /// <summary>The right half of a two-column character.</summary>
    public static TuiCell Continuation(TuiStyle style) => new(string.Empty, style);

    /// <summary>Whether this cell is the second half of a wide character.</summary>
    public bool IsContinuation => Text.Length == 0;
}

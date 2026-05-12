namespace Tosh.Tui.Editing;

/// <summary>
/// A run of characters within a single line that share one style. Coordinates
/// are byte offsets within the line (no '\n'). <see cref="AnsiOpen"/> is the
/// SGR sequence to emit before the run; the renderer appends the reset.
/// </summary>
public readonly record struct StyledSpan(int Start, int Length, string AnsiOpen)
{
    public int End => Start + Length;
}

/// <summary>
/// Produces per-line styled spans for a document. Implementations may be
/// stateless (lex each line independently) or carry state for languages that
/// need cross-line context.
/// </summary>
public interface ISyntaxColorizer
{
    /// <summary>
    /// Returns styled spans for <paramref name="line"/>. Spans must be
    /// non-overlapping and ordered by <see cref="StyledSpan.Start"/>.
    /// Gaps between spans render with the default terminal style.
    /// </summary>
    IReadOnlyList<StyledSpan> Colorize(string line, int lineIndex);
}

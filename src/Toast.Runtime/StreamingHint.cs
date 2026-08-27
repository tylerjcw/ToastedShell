namespace Tosh.Runtime;

/// <summary>
/// Controls whether the streaming display path is used for a pipeline whose row type
/// matches this profile.
/// </summary>
public enum StreamingHint
{
    /// <summary>
    /// The display engine decides: buffer if output arrives within ~250 ms (fast commands),
    /// stream otherwise.
    /// </summary>
    Auto,

    /// <summary>
    /// Every column declares a fixed <see cref="DisplayTableColumn.StreamWidth"/>, so the
    /// table header can be drawn before any rows arrive and widths never need rewriting.
    /// </summary>
    FixedWidth,

    /// <summary>
    /// Always collect all rows before rendering.  Use for profiles whose cell values vary
    /// wildly in width and where re-rendering prior rows would be disruptive.
    /// </summary>
    NeverStream,
}

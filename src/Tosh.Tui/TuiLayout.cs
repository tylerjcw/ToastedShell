namespace Tosh.Tui;

/// <summary>Layout orientation for a TUI screen.</summary>
public enum TuiLayout
{
    /// <summary>A single widget fills the entire screen.</summary>
    Single,

    /// <summary>Two panes arranged side by side (left | right).</summary>
    SplitHorizontal,

    /// <summary>Two panes arranged top over bottom.</summary>
    SplitVertical,

    /// <summary>Widgets stacked vertically, each taking a proportional share.</summary>
    Stacked,
}

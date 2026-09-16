namespace Tosh.Tui.Widgets;

/// <summary>
/// A widget that can put something on the layer above the page.
/// </summary>
/// <remarks>
/// <para>
/// A menu's drop-down, a combo box's list and anything else that opens out of a control
/// are the same thing wearing different labels: a small widget shown over the page,
/// anchored to whatever it came out of, holding the keyboard while it is up.
/// </para>
/// <para>
/// A widget cannot draw one itself — it cannot paint outside what it was given, which is
/// the whole reason layers exist — so it says what it wants and whatever owns the tree
/// puts it there. One interface rather than a second implementation per control is the
/// point: the combo box exists because a drop-down already did (<c>TUI-0023</c>).
/// </para>
/// </remarks>
public interface ITuiPopupHost
{
    /// <summary>What to show above the page, or null when nothing is open.</summary>
    TuiWidget? Popup { get; }

    /// <summary>What it opens from, so it can be drawn next to it rather than centred.</summary>
    TuiWidget? PopupAnchor { get; }

    /// <summary>Takes it down. Says whether anything was up.</summary>
    bool ClosePopup();
}

using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>
/// The keys a screen answers, drawn from the bindings rather than written beside them.
/// </summary>
/// <remarks>
/// <para>
/// A footer used to be a string, maintained by hand next to the <c>switch</c> that
/// implemented the keys. It drifted, as that always does: the help browser offered
/// <c>/</c>, <c>[</c> and <c>]</c> for a while after they had stopped working
/// (<c>TUI-0022</c>).
/// </para>
/// <para>
/// This is a projection instead. It shows the keys that would answer <em>right now</em> —
/// a binding with a condition appears only where the condition holds, and one nested in a
/// dialog appears only while the dialog is up — so the footer cannot claim a key the
/// screen would ignore.
/// </para>
/// <para>
/// One line by default, which is what a footer is. <see cref="Full"/> turns it into the
/// list a reader asks for when the line is not enough.
/// </para>
/// </remarks>
public sealed class TuiHelp : TuiWidget
{
    /// <summary>The tables to describe. Usually filled in by the screen.</summary>
    /// <remarks>
    /// A screen has many tables — one per widget that registered keys — and which of them
    /// answer depends on where the keyboard is. Asking the screen rather than holding a
    /// list is what keeps the footer honest when a dialog goes up.
    /// </remarks>
    public Func<IEnumerable<TuiShortcuts>>? Tables { get; set; }

    /// <summary>A table to describe, for a caller that has just the one.</summary>
    /// <remarks>
    /// Not <c>Keys</c>: every widget already has one of those — the keys it <em>answers</em> —
    /// and a property of the same name meaning the keys it <em>describes</em> is two ideas
    /// wearing one word.
    /// </remarks>
    public TuiShortcuts? Table { get; set; }

    /// <summary>Whether to draw the list rather than the line.</summary>
    public bool Full { get; set; }

    /// <summary>Asked each redraw whether to draw the list, when the answer can change.</summary>
    /// <remarks>
    /// A hook rather than a bound property: "show me everything" is a thing a key toggles,
    /// and the widget should read the answer rather than be told it.
    /// </remarks>
    public Func<bool>? FullWhen { get; set; }

    private bool ShowsEverything => FullWhen?.Invoke() ?? Full;

    /// <summary>What separates one key from the next on the line.</summary>
    public string Separator { get; set; } = "   ";

    /// <summary>How the key itself is drawn.</summary>
    public TuiStyle LabelStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>How what it does is drawn.</summary>
    public TuiStyle Style { get; set; }

    /// <inheritdoc />
    public override object? Value => Line();

    /// <summary>
    /// Every key that would answer now, nearest the keyboard first, each chord once.
    /// </summary>
    /// <remarks>
    /// A chord bound twice — a dialog's Escape over the screen's — is one key to a reader,
    /// and the one that would answer is the nearer one. Listing both would describe a
    /// screen nobody is using.
    /// </remarks>
    public IReadOnlyList<TuiShortcut> Describes()
    {
        var seen = new HashSet<(ConsoleKey, char, ConsoleModifiers)>();
        var found = new List<TuiShortcut>();

        foreach (var table in Sources())
        {
            foreach (var shortcut in table.Available)
            {
                if (shortcut.Description.Length > 0 &&
                    seen.Add((shortcut.Key, shortcut.Character, shortcut.Modifiers)))
                {
                    found.Add(shortcut);
                }
            }
        }

        return found;
    }

    private IEnumerable<TuiShortcuts> Sources()
    {
        if (Table is { } only)
        {
            yield return only;
        }

        foreach (var table in Tables?.Invoke() ?? [])
        {
            yield return table;
        }
    }

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var keys = Describes();

        if (!ShowsEverything)
        {
            return constraints.Constrain(new TuiSize(TextMeasure.MeasureWidth(Line()), keys.Count == 0 ? 0 : 1));
        }

        var width = keys.Count == 0
            ? 0
            : keys.Max(key => TextMeasure.MeasureWidth(key.Label) + TextMeasure.MeasureWidth(key.Description) + 3);

        return constraints.Constrain(new TuiSize(width, keys.Count));
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        var keys = Describes();

        if (!ShowsEverything)
        {
            DrawLine(surface, keys);
            return;
        }

        // The label column is as wide as the widest key, so the descriptions line up and
        // the list reads as a table rather than as sentences of different lengths.
        var column = keys.Count == 0 ? 0 : keys.Max(key => TextMeasure.MeasureWidth(key.Label));

        for (var row = 0; row < keys.Count && row < surface.Height; row += 1)
        {
            surface.DrawText(0, row, keys[row].Label, LabelStyle);
            surface.DrawText(column + 2, row, keys[row].Description, Style);
        }
    }

    private void DrawLine(TuiSurface surface, IReadOnlyList<TuiShortcut> keys)
    {
        var column = 0;

        for (var index = 0; index < keys.Count && column < surface.Width; index += 1)
        {
            if (index > 0)
            {
                column += surface.DrawText(column, 0, Separator, Style);
            }

            column += surface.DrawText(column, 0, keys[index].Label, LabelStyle);
            column += surface.DrawText(column, 0, " ", Style);
            column += surface.DrawText(column, 0, keys[index].Description, Style);
        }
    }

    private string Line()
        => string.Join(Separator, Describes().Select(key => $"{key.Label} {key.Description}"));
}

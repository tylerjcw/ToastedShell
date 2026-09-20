using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>Which of these to use, and in what order.</summary>
/// <remarks>
/// <para>
/// `TUI-0002`. <see cref="TuiOrderedToggleEditorState{TItem}"/> keeps the order, keeps which
/// entries are in, refuses a toggle that would empty the list past its minimum, and reads
/// the keys — and does not draw. The config browser used it for the prompt layout and drew
/// the rows itself.
/// </para>
/// <para>
/// The state asks for an <c>includedUpdater</c> that returns a *new* item carrying the new
/// state, which suits a record with an <c>Included</c> field and suits nothing a script is
/// likely to hand it — a list of strings cannot carry a flag. So inclusion is tracked beside
/// the entries, by key, and the updater leaves the entry alone. Strings, records and
/// dictionaries all work, and the entries a caller passed in are the entries it gets back.
/// </para>
/// </remarks>
public sealed class TuiArrange : TuiWidget
{
    private readonly TuiOrderedToggleEditorState<object?> _state = new();
    private readonly HashSet<string> _included = new(StringComparer.Ordinal);

    public TuiArrange(IEnumerable<object?>? entries = null)
    {
        Entries = entries is null ? [] : [.. entries];
    }

    /// <summary>Every entry offered, in its current order.</summary>
    /// <remarks>Setting this puts every entry in; <see cref="Included"/> narrows it after.</remarks>
    public IReadOnlyList<object?> Entries
    {
        get => _state.Items;
        set
        {
            var entries = value ?? [];

            _included.Clear();

            foreach (var entry in entries)
            {
                _included.Add(Key(entry));
            }

            Reopen(entries);
        }
    }

    /// <summary>The entries that are in, in the order they are drawn.</summary>
    public IReadOnlyList<object?> Included
        => [.. _state.Items.Where(entry => _included.Contains(Key(entry)))];

    /// <summary>A property read off each entry for its label.</summary>
    public string? DisplayProperty { get; set; }

    /// <summary>How few entries may be left in. A toggle that would go below this is refused.</summary>
    /// <remarks>
    /// Re-opens the state, because the state takes the minimum when it is opened and an
    /// object initializer runs after the constructor — so a widget built as
    /// <c>new TuiArrange(entries) { MinimumIncluded = 1 }</c> would otherwise keep the zero
    /// it was opened with and let the last entry leave.
    /// </remarks>
    public int MinimumIncluded
    {
        get => _minimumIncluded;
        set
        {
            _minimumIncluded = value;

            // Snapshot: `Open` clears its own list before refilling it, so handing it that
            // same list empties the widget.
            Reopen([.. _state.Items]);
        }
    }

    private int _minimumIncluded;

    /// <summary>Raised with the included entries, in order, whenever either changes.</summary>
    public Action<IReadOnlyList<object?>>? Changed { get; set; }

    /// <summary>Raised when a toggle is refused for leaving too few entries in.</summary>
    public Action<object?>? Refused { get; set; }

    /// <summary>Raised with the included entries when <c>Enter</c> settles the arrangement.</summary>
    public Action<IReadOnlyList<object?>>? Committed { get; set; }

    /// <summary>Raised when <c>Esc</c> abandons it.</summary>
    public Action? Cancelled { get; set; }

    /// <summary>Whether committing or cancelling ends the screen. Off by default.</summary>
    public bool ClosesOnCommit { get; set; }

    public TuiStyle Style { get; set; }

    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    public override bool IsFocusable => true;

    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var widest = 0;

        foreach (var entry in _state.Items)
        {
            widest = Math.Max(widest, TextMeasure.MeasureWidth(Format(entry)));
        }

        return constraints.Constrain(new TuiSize(widest + 6, Math.Max(1, _state.Items.Count)));
    }

    public override void Draw(TuiSurface surface)
    {
        var entries = _state.Items;

        for (var row = 0; row < entries.Count && row < surface.Height; row++)
        {
            var isSelected = row == _state.SelectedIndex;
            var style = isSelected ? SelectedStyle : Style;
            var marker = isSelected ? "> " : "  ";
            var box = _included.Contains(Key(entries[row])) ? "[x] " : "[ ] ";

            var used = surface.DrawText(0, row, marker + box, style);
            surface.DrawText(used, row, Format(entries[row]), style);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The keys are the state's: the arrows move the cursor, <c>Shift</c> with them moves the
    /// entry, <c>Space</c> toggles it, <c>Enter</c> settles and <c>Esc</c> abandons.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return false;
        }

        var before = _state.SelectedKey;
        var action = _state.HandleKey(input.Key);

        switch (action.Kind)
        {
            case TuiOrderedToggleEditorActionKind.Toggled:
            case TuiOrderedToggleEditorActionKind.Reordered:
                Changed?.Invoke(Included);
                return true;

            case TuiOrderedToggleEditorActionKind.ToggleRejected:
                Refused?.Invoke(action.Item);
                return true;

            case TuiOrderedToggleEditorActionKind.Commit:
                Committed?.Invoke(Included);

                if (ClosesOnCommit)
                {
                    ClosesScreen = true;
                }

                return true;

            case TuiOrderedToggleEditorActionKind.Cancel:
                Cancelled?.Invoke();

                if (ClosesOnCommit)
                {
                    ClosesScreen = true;
                }

                return true;

            case TuiOrderedToggleEditorActionKind.SelectionUnavailable:
                // Nothing to arrange. Handled anyway: the key was meant for this widget, and
                // passing it on would act on whatever is behind it.
                return true;

            default:
                return !string.Equals(_state.SelectedKey, before, StringComparison.Ordinal);
        }
    }

    private void Reopen(IReadOnlyList<object?> entries)
        => _state.Open(
            entries,
            Math.Max(1, entries.Count),
            Key,
            entry => _included.Contains(Key(entry)),
            (entry, included) =>
            {
                // The entry is returned unchanged; what changed is what this widget knows
                // about it. That is the whole reason inclusion lives here.
                if (included)
                {
                    _included.Add(Key(entry));
                }
                else
                {
                    _included.Remove(Key(entry));
                }

                return entry;
            },
            _state.SelectedKey,
            Math.Max(0, _minimumIncluded));

    private string Key(object? entry) => Format(entry);

    private string Format(object? entry) => TuiList.FormatValue(entry, DisplayProperty);
}

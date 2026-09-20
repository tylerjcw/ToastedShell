using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A list of named values, each one toggled or edited in place.</summary>
/// <remarks>
/// <para>
/// `TUI-0002`. <see cref="TuiGroupEditorState{TItem}"/> keeps the cursor, keeps its key so a
/// redraw finds the same row, and reads the keys — and does not draw. The config browser
/// assembled its sub-editor inline, a row at a time.
/// </para>
/// <para>
/// This does not act on the rows itself, unlike <see cref="TuiCollectionEditor"/>. A
/// collection is a list of values and "add this one" means the same thing everywhere; a
/// group is a list of *settings*, and what toggling one means belongs to whoever defined it.
/// So the widget reports which row was asked about and leaves the answer to the caller.
/// </para>
/// </remarks>
public sealed class TuiGroupEditor : TuiWidget
{
    private readonly TuiGroupEditorState<object?> _state = new();
    private IReadOnlyList<object?> _rows = [];

    public TuiGroupEditor(IEnumerable<object?>? rows = null)
    {
        Rows = rows is null ? [] : [.. rows];
    }

    /// <summary>The rows offered, in the order they are drawn.</summary>
    public IReadOnlyList<object?> Rows
    {
        get => _rows;
        set
        {
            _rows = value ?? [];
            _state.Open(_rows, Math.Max(1, _rows.Count), Label, _state.SelectedKey);
        }
    }

    /// <summary>A property read off each row for its name.</summary>
    public string? LabelProperty { get; set; }

    /// <summary>A property read off each row for the value shown beside its name.</summary>
    public string? ValueProperty { get; set; }

    /// <summary>The row under the cursor.</summary>
    public object? Selected => _state.TryGetSelected(out var row) ? row : null;

    /// <summary>Raised with the row when <c>Space</c> asks for it to be toggled.</summary>
    public Action<object?>? Toggled { get; set; }

    /// <summary>Raised with the row when <c>Enter</c> or <c>e</c> asks to edit it.</summary>
    public Action<object?>? Edited { get; set; }

    /// <summary>Raised with the row when <c>t</c> asks to edit it as raw text.</summary>
    public Action<object?>? RawEdited { get; set; }

    /// <summary>Raised when <c>Esc</c> closes the group.</summary>
    public Action? Closed { get; set; }

    /// <summary>Whether closing ends the screen. Off by default, as a field's would be.</summary>
    public bool ClosesOnClose { get; set; }

    public TuiStyle Style { get; set; }

    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    public override bool IsFocusable => true;

    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var widest = 0;

        foreach (var row in _rows)
        {
            widest = Math.Max(widest, TextMeasure.MeasureWidth(RowText(row)));
        }

        return constraints.Constrain(new TuiSize(widest + 2, Math.Max(1, _rows.Count)));
    }

    public override void Draw(TuiSurface surface)
    {
        var visible = _state.GetVisibleItems();

        for (var row = 0; row < visible.Count && row < surface.Height; row++)
        {
            var entry = visible[row];
            var style = entry.IsSelected ? SelectedStyle : Style;
            var used = surface.DrawText(0, row, entry.IsSelected ? "> " : "  ", style);

            surface.DrawText(used, row, RowText(entry.Item), style);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The keys are the state's, so the arrows, Home, End, <c>Space</c>, <c>Enter</c>,
    /// <c>e</c>, <c>t</c> and <c>Esc</c> mean here what they mean in the config browser.
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
            case TuiGroupEditorActionKind.ToggleSelected:
                Toggled?.Invoke(action.Item);
                return true;

            case TuiGroupEditorActionKind.EditSelected:
                Edited?.Invoke(action.Item);
                return true;

            case TuiGroupEditorActionKind.RawEditSelected:
                RawEdited?.Invoke(action.Item);
                return true;

            case TuiGroupEditorActionKind.Close:
                Closed?.Invoke();

                if (ClosesOnClose)
                {
                    ClosesScreen = true;
                }

                return true;

            case TuiGroupEditorActionKind.SelectionUnavailable:
                // Asked about a row that is not there. Handled anyway: the key was meant for
                // the group, and passing it on would act on whatever is behind it.
                return true;

            default:
                return !string.Equals(_state.SelectedKey, before, StringComparison.Ordinal);
        }
    }

    /// <summary>A row as <c>name: value</c>, or just its name when there is no value.</summary>
    private string RowText(object? row)
    {
        var label = Label(row);

        if (ValueProperty is not { Length: > 0 })
        {
            return label;
        }

        var value = TuiList.FormatValue(row, ValueProperty);

        return value.Length == 0 ? label : $"{label}: {value}";
    }

    private string Label(object? row) => TuiList.FormatValue(row, LabelProperty);
}

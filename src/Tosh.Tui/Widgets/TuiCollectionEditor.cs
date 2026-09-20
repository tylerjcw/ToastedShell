using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A list you can add to, edit and remove from.</summary>
/// <remarks>
/// <para>
/// `TUI-0002`. <see cref="TuiCollectionEditorState{TItem}"/> tracks the cursor, the input
/// mode and the text being typed, and reads the keys — and does not draw. The config browser
/// drew its collection editor into a detail pane, a row at a time plus an input line. This
/// is that drawing, owned by a widget and wrapping the same state.
/// </para>
/// <para>
/// It differs from the browser's use in one deliberate way: <b>it applies the edit itself</b>.
/// The state reports "the reader submitted this text" and leaves the mutation to its owner,
/// which is right for a config browser staging changes against a schema, and wrong for a
/// script that wants a list edited. A script would otherwise have to implement add, replace
/// and remove before it could ask for a list of tags.
/// </para>
/// </remarks>
public sealed class TuiCollectionEditor : TuiWidget
{
    private readonly TuiCollectionEditorState<object?> _state = new();
    private readonly List<object?> _items = [];

    public TuiCollectionEditor(IEnumerable<object?>? items = null)
    {
        Items = items is null ? [] : [.. items];
    }

    /// <summary>The list being edited. Replacing it starts again from the top.</summary>
    public IReadOnlyList<object?> Items
    {
        get => _items;
        set
        {
            _items.Clear();
            _items.AddRange(value ?? []);
            Reopen();
        }
    }

    /// <summary>A property read off each item for its label, rather than the whole value.</summary>
    public string? DisplayProperty { get; set; }

    /// <summary>Raised with the whole list whenever it changes.</summary>
    /// <remarks>
    /// The list rather than the change: a caller almost always wants the result, and one
    /// given only the delta has to keep its own copy in step to find out what it now holds.
    /// </remarks>
    public Action<IReadOnlyList<object?>>? Changed { get; set; }

    /// <summary>Raised when <c>Esc</c> closes the editor without a pending input.</summary>
    public Action? Closed { get; set; }

    /// <summary>Whether closing ends the screen. Off by default, as a field's would be.</summary>
    public bool ClosesOnClose { get; set; }

    public TuiStyle Style { get; set; }

    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>Whether the reader is typing, rather than moving between rows.</summary>
    public bool IsEditing => _state.InputMode != TuiCollectionEditorInputMode.None;

    public override bool IsFocusable => true;

    /// <summary>A row per item, and one more for the input line while it is up.</summary>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var widest = 0;

        foreach (var item in _items)
        {
            widest = Math.Max(widest, TextMeasure.MeasureWidth(Format(item)));
        }

        return constraints.Constrain(
            new TuiSize(widest + 2, Math.Max(1, _items.Count + (IsEditing ? 1 : 0))));
    }

    public override void Draw(TuiSurface surface)
    {
        var visible = _state.GetVisibleItems();
        var row = 0;

        for (; row < visible.Count && row < surface.Height; row++)
        {
            var entry = visible[row];
            var style = entry.IsSelected ? SelectedStyle : Style;
            var used = surface.DrawText(0, row, entry.IsSelected ? "> " : "  ", style);

            surface.DrawText(used, row, Format(entry.Item), style);
        }

        if (!IsEditing || row >= surface.Height)
        {
            return;
        }

        // The cursor is drawn into the text rather than positioned, because a cell grid has
        // no cursor of its own — the same way the config browser showed it.
        var prompt = _state.InputMode == TuiCollectionEditorInputMode.AddItem ? "+ " : "= ";
        var promptUsed = surface.DrawText(0, row, prompt, SelectedStyle);

        surface.DrawText(promptUsed, row, _state.RenderInputWithCursor(), SelectedStyle);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The keys are the state's: the arrows and pages move, <c>n</c> adds, <c>Enter</c> or
    /// <c>e</c> edits, <c>Delete</c> or <c>r</c> removes, and <c>Esc</c> closes — or, while
    /// typing, abandons the line rather than the editor.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
        {
            return false;
        }

        var editingBefore = IsEditing;
        var action = _state.HandleKey(input.Key);

        switch (action.Kind)
        {
            case TuiCollectionEditorActionKind.SubmitInput:
                Submit(action);
                return true;

            case TuiCollectionEditorActionKind.RemoveItem:
                Remove(action.Item);
                return true;

            case TuiCollectionEditorActionKind.InputCancelled:
                _state.CancelInput();
                Reopen();
                return true;

            case TuiCollectionEditorActionKind.Close:
                Closed?.Invoke();

                if (ClosesOnClose)
                {
                    ClosesScreen = true;
                }

                return true;

            case TuiCollectionEditorActionKind.EditUnavailable:
            case TuiCollectionEditorActionKind.RemoveUnavailable:
                // Nothing to edit or remove. Handled anyway: the key was meant for the
                // editor, and passing it on would act on whatever is behind it.
                return true;

            default:
                // `n` and `e` open the input without reporting an action, so "the editor did
                // something" is the mode changing, not the action kind.
                return IsEditing != editingBefore || Moved(input.Key);
        }
    }

    private void Submit(TuiCollectionEditorAction<object?> action)
    {
        var text = action.Text ?? string.Empty;

        if (action.InputMode == TuiCollectionEditorInputMode.AddItem)
        {
            _items.Add(text);
        }
        else
        {
            var index = _items.FindIndex(item => string.Equals(Format(item), action.Key, StringComparison.Ordinal));

            if (index >= 0)
            {
                _items[index] = text;
            }
        }

        _state.CompleteInput(text);
        Reopen(text);
        Changed?.Invoke(Items);
    }

    private void Remove(object? item)
    {
        var index = _items.FindIndex(candidate => string.Equals(Format(candidate), Format(item), StringComparison.Ordinal));

        if (index < 0)
        {
            return;
        }

        _items.RemoveAt(index);
        Reopen();
        Changed?.Invoke(Items);
    }

    private static bool Moved(ConsoleKeyInfo key)
        => key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp
            or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End;

    private void Reopen(string? preferredKey = null)
        => _state.Open(
            _items,
            Math.Max(1, _items.Count),
            Format,
            item => Format(item),
            preferredKey);

    private string Format(object? item) => TuiList.FormatValue(item, DisplayProperty);
}

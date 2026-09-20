using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>One choice out of several, drawn as radio rows.</summary>
/// <remarks>
/// <para>
/// `TUI-0002`. <see cref="TuiOptionPickerState{TItem}"/> tracks which option is chosen, keeps
/// a key so a redraw can find it again, and reads the keys — and does not draw. The config
/// browser drew its enum picker by hand, a row at a time, building
/// <c>"&gt; (*) Name"</c> in the middle of a detail pane. This is that drawing, owned by a
/// widget.
/// </para>
/// <para>
/// Not the same thing as <see cref="TuiList"/>, which is a list you move through and
/// activate. This is a question with one answer: the marker says which option *is* the
/// value, <c>Enter</c> settles it and <c>Esc</c> abandons it.
/// </para>
/// </remarks>
public sealed class TuiChoice : TuiWidget
{
    private readonly TuiOptionPickerState<object?> _state = new();
    private IReadOnlyList<object?> _options = [];

    public TuiChoice(IEnumerable<object?>? options = null)
    {
        Options = options is null ? [] : [.. options];
    }

    /// <summary>The options offered, in the order they are drawn.</summary>
    /// <remarks>
    /// Re-opening the state rather than mutating it keeps the chosen option across a change
    /// where the same one is still offered, which is what its key is for.
    /// </remarks>
    public IReadOnlyList<object?> Options
    {
        get => _options;
        set
        {
            _options = value ?? [];
            _state.Open(_options, Math.Max(1, _options.Count), Format, _state.SelectedKey);
        }
    }

    /// <summary>A property read off each option for its label, rather than the whole value.</summary>
    public string? DisplayProperty { get; set; }

    /// <summary>The option currently marked, or null when there are none.</summary>
    public object? Selected => _state.TryGetSelected(out var item) ? item : null;

    /// <summary>The index of the marked option.</summary>
    public int SelectedIndex => _state.SelectedIndex;

    /// <summary>Raised with the option when <c>Enter</c> settles it.</summary>
    public Action<object?>? Chosen { get; set; }

    /// <summary>Raised when <c>Esc</c> abandons the choice without making one.</summary>
    public Action? Cancelled { get; set; }

    /// <summary>Raised as the marker moves, before anything is settled.</summary>
    public Action<object?>? SelectionChanged { get; set; }

    /// <summary>
    /// Whether settling a choice closes the screen. Off by default.
    /// </summary>
    /// <remarks>
    /// Opt-in, the way a button's <c>Exit</c> is and unlike <see cref="TuiConfirm"/>'s: a
    /// picker is usually one field among several, and a form with two of them that ended on
    /// the first would never reach the second. A script asking one question and nothing else
    /// turns it on.
    /// </remarks>
    public bool ClosesOnChoose { get; set; }

    public TuiStyle Style { get; set; }

    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    public override bool IsFocusable => true;

    /// <summary>One row per option, as wide as the widest of them plus the marker.</summary>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var widest = 0;

        foreach (var option in _options)
        {
            widest = Math.Max(widest, TextMeasure.MeasureWidth(Format(option)));
        }

        return constraints.Constrain(new TuiSize(widest + 6, Math.Max(1, _options.Count)));
    }

    public override void Draw(TuiSurface surface)
    {
        for (var index = 0; index < _options.Count && index < surface.Height; index++)
        {
            var isSelected = index == _state.SelectedIndex;
            var marker = isSelected ? "> " : "  ";
            var radio = isSelected ? "(*) " : "( ) ";
            var style = isSelected ? SelectedStyle : Style;

            var used = surface.DrawText(0, index, marker + radio, style);
            surface.DrawText(used, index, Format(_options[index]), style);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The keys are the state's, so the arrows, the pages, Home, End, Enter and Escape mean
    /// here what they mean in the config browser's enum picker.
    /// </remarks>
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey || _options.Count == 0)
        {
            return false;
        }

        var before = _state.SelectedIndex;
        var action = _state.HandleKey(input.Key);

        switch (action.Kind)
        {
            case TuiOptionPickerActionKind.Commit:
                Chosen?.Invoke(action.Item);

                if (ClosesOnChoose)
                {
                    ClosesScreen = true;
                }

                return true;
            case TuiOptionPickerActionKind.Cancel:
                Cancelled?.Invoke();
                return true;
            case TuiOptionPickerActionKind.SelectionUnavailable:
                // Enter with nothing to settle. Handled rather than passed on: the key was
                // meant for the picker, and letting it through would fire whatever is behind.
                return true;
            default:
                if (_state.SelectedIndex == before)
                {
                    return false;
                }

                SelectionChanged?.Invoke(Selected);
                return true;
        }
    }

    /// <summary>The label for an option — its display property, or the value itself.</summary>
    /// <remarks>
    /// <see cref="TuiList.FormatValue"/> rather than a second copy: a script's options are
    /// normally records or dictionaries, whose fields are not CLR properties, so reflection
    /// alone finds nothing on them and every row reads as a type name. That one already
    /// asks shell records first and falls back to reflection, and a picker labelling its
    /// options differently from a list would be a difference nobody asked for.
    /// </remarks>
    private string Format(object? item) => TuiList.FormatValue(item, DisplayProperty);
}

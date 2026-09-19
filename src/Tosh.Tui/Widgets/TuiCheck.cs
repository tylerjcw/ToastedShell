using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A boolean checkbox that can be toggled.</summary>
public sealed class TuiCheck : TuiWidget
{
    public TuiCheck(string label, bool isChecked = false, Action<bool>? toggled = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        Label = label;
        IsChecked = isChecked;
        Toggled = toggled;
    }

    public string Label { get; set; }

    public bool IsChecked { get; set; }

    /// <summary>Raised when the checkbox is toggled.</summary>
    public Action<bool>? Toggled { get; set; }

    public bool IsSelected { get; set; }

    protected override void OnFocusChanged() => IsSelected = IsFocused;

    public TuiStyle Style { get; set; }

    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    public override bool IsFocusable => true;

    /// <summary>Two cells for the marker, four for the box, and the label.</summary>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(TuiTextMeasure.MeasureWidth(Label) + 6, 1));

    public override void Draw(TuiSurface surface)
    {
        var marker = IsSelected ? "> " : "  ";
        var box = IsChecked ? "[x] " : "[ ] ";
        var used = surface.DrawText(0, 0, marker + box, IsSelected ? SelectedStyle : Style);

        surface.DrawText(used, 0, Label, IsSelected ? SelectedStyle : Style);
    }

    public override bool OnInput(TuiInputEvent input)
    {
        if (input.IsKey)
        {
            if (!IsSelected || input.Key.Key is not (ConsoleKey.Enter or ConsoleKey.Spacebar))
            {
                return false;
            }

            IsChecked = !IsChecked;
            Toggled?.Invoke(IsChecked);
            return true;
        }

        // `IsMouse`, not "not a key": a paste and a focus report are neither, and reading
        // `input.Mouse` on one gives a default struct whose action is Press and whose button
        // is Left — a click at the origin, which is where a checkbox often sits.
        if (!input.IsMouse)
        {
            return false;
        }

        var mouse = input.Mouse;

        if (mouse.Action != TuiMouseAction.Press ||
            mouse.Button != TuiMouseButton.Left ||
            !Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        IsSelected = true;
        IsChecked = !IsChecked;
        Toggled?.Invoke(IsChecked);
        return true;
    }
}

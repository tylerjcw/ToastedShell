using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A labelled button that can be selected and pressed.</summary>
/// <remarks>
/// Drawn as <c>[Label]</c>, with a marker in front when selected so the choice is legible
/// on a terminal with no colour as well as one with it.
/// </remarks>
public sealed class TuiButton : TuiWidget
{
    public TuiButton(string label, Action? pressed = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        Label = label;
        Pressed = pressed;
    }

    public string Label { get; set; }

    /// <summary>Raised when the button is pressed, by key or by click.</summary>
    public Action? Pressed { get; set; }

    /// <summary>Whether this is the button the keyboard will act on.</summary>
    /// <remarks>
    /// Follows focus wherever a screen has a focus manager, so a dialog's buttons need
    /// nothing written on them: the one the keyboard is on is the one that draws a marker
    /// and the one Enter presses. A screen that routes keys itself sets this by hand — the
    /// confirmation prompt does, and toggles it with the arrow keys.
    /// </remarks>
    public bool IsSelected { get; set; }

    /// <inheritdoc />
    protected override void OnFocusChanged() => IsSelected = IsFocused;

    public TuiStyle Style { get; set; }

    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    public override bool IsFocusable => true;

    /// <summary>Two cells for the marker, two for the brackets, and the label.</summary>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(TuiTextMeasure.MeasureWidth(Label) + 4, 1));

    public override void Draw(TuiSurface surface)
    {
        var marker = IsSelected ? "> " : "  ";
        var used = surface.DrawText(0, 0, marker, Style);

        surface.DrawText(used, 0, $"[{Label}]", IsSelected ? SelectedStyle : Style);
    }

    public override bool OnInput(TuiInputEvent input)
    {
        if (input.IsKey)
        {
            if (!IsSelected || input.Key.Key is not (ConsoleKey.Enter or ConsoleKey.Spacebar))
            {
                return false;
            }

            Pressed?.Invoke();
            return true;
        }

        var mouse = input.Mouse;

        // A click anywhere on the button selects and presses it. Hit testing has already
        // decided this event is ours; the bounds check is for a screen routing broadly.
        if (mouse.Action != TuiMouseAction.Press ||
            mouse.Button != TuiMouseButton.Left ||
            !Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        IsSelected = true;
        Pressed?.Invoke();
        return true;
    }
}

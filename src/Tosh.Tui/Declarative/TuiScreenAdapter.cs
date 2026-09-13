using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tui.Declarative;

/// <summary>
/// Builds a widget tree from a <see cref="TuiScreen"/> assembled by the <c>tui</c>
/// builder subcommands.
/// </summary>
/// <remarks>
/// <para>
/// The builder — <c>tui screen</c>, <c>tui add-list</c>, <c>tui layout</c> — is on its
/// way out: it exists only because a widget tree could not be written as a value, and it
/// can be (<c>TUI-0015</c>). Until it goes, both forms come here, so there is one screen
/// implementation rather than two.
/// </para>
/// <para>
/// Retiring the interpreter it replaces is the point. That was 753 lines of switch over
/// a six-value enum, and the enum is what made the set of widgets a script could use
/// closed. Screens built the old way now get the widgets, the layout and the focus
/// handling the new way has, which is a better fate for them than being frozen until the
/// builder is deleted.
/// </para>
/// </remarks>
public static class TuiScreenAdapter
{
    /// <summary>Builds the widget tree a built screen describes.</summary>
    public static TuiWidget BuildTree(TuiScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var widgets = screen.Widgets.Select(ToWidget).Where(widget => widget is not null).Select(widget => widget!).ToArray();

        return Arrange(widgets, screen.LayoutConfig);
    }

    /// <summary>Lays the widgets out the way the screen's layout asked for.</summary>
    /// <remarks>
    /// The four old arrangements map onto one container. A ratio like <c>"1:2"</c> becomes
    /// star weights, which is what it always meant.
    /// </remarks>
    private static TuiWidget Arrange(IReadOnlyList<TuiWidget> widgets, TuiLayoutConfig layout)
    {
        if (widgets.Count == 0)
        {
            return new TuiTextWidget("(empty screen)");
        }

        if (widgets.Count == 1 || layout.Layout == TuiLayout.Single)
        {
            return widgets[0];
        }

        var orientation = layout.Layout == TuiLayout.SplitHorizontal
            ? TuiOrientation.Horizontal
            : TuiOrientation.Vertical;

        var stack = new TuiStack(orientation) { Gap = layout.Gap };
        var weights = ParseRatio(layout.Ratio, widgets.Count);

        for (var index = 0; index < widgets.Count; index += 1)
        {
            stack.Add(widgets[index], TuiLength.Star(weights[index]));
        }

        return stack;
    }

    private static int[] ParseRatio(string? ratio, int count)
    {
        var weights = Enumerable.Repeat(1, count).ToArray();

        if (string.IsNullOrWhiteSpace(ratio))
        {
            return weights;
        }

        var parts = ratio.Split(':', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < Math.Min(parts.Length, count); index += 1)
        {
            if (int.TryParse(parts[index].Trim(), out var weight) && weight > 0)
            {
                weights[index] = weight;
            }
        }

        return weights;
    }

    /// <summary>Maps one builder config onto the widget that now draws it.</summary>
    private static TuiWidget? ToWidget(ITuiWidget config)
    {
        switch (config)
        {
            case TuiListWidgetConfig list:
            {
                var widget = new TuiList(list.Items)
                {
                    Id = list.Id,
                    DisplayProperty = list.DisplayProperty,
                    MultiSelect = list.MultiSelect,
                };

                return Titled(widget, list.Prompt);
            }

            case TuiTextWidgetConfig text:
                return new TuiTextWidget(text.Content?.ToString() ?? string.Empty)
                {
                    Id = text.Id,
                    Wrap = text.WordWrap,
                };

            case TuiTextInputConfig input:
            {
                var field = new TuiTextField(input.DefaultValue ?? string.Empty)
                {
                    Id = input.Id,
                    Multiline = input.Multiline,
                };

                return Labelled(field, input.Prompt);
            }

            case TuiOptionPickerConfig picker:
            {
                var widget = new TuiList(picker.Options)
                {
                    Id = picker.Id,
                    DisplayProperty = picker.DisplayProperty,
                };

                return Titled(widget, picker.Prompt);
            }

            case TuiConfirmationConfig confirm:
            {
                var list = new TuiList([confirm.ConfirmLabel, confirm.CancelLabel]) { Id = confirm.Id };

                if (!confirm.DefaultConfirm)
                {
                    list.SelectedIndex = 1;
                }

                return Titled(list, confirm.Message);
            }

            case TuiFilePickerConfig picker:
                // A file picker inside a composed screen has never been more than a
                // placeholder; it is a screen of its own, not a widget.
                return new TuiTextWidget($"[file picker: {picker.InitialPath ?? "."}]") { Id = picker.Id };

            default:
                return null;
        }
    }

    private static TuiWidget Titled(TuiWidget widget, string? title)
        => string.IsNullOrWhiteSpace(title)
            ? widget
            : new TuiBorder(widget, title) { TitleStyle = new TuiStyle(Attributes: TuiTextAttributes.Bold) };

    private static TuiWidget Labelled(TuiWidget widget, string? label)
        => string.IsNullOrWhiteSpace(label)
            ? widget
            : new TuiStack(TuiOrientation.Horizontal)
                .Add(
                    new TuiTextWidget($"{label}: ") { Style = new TuiStyle(Attributes: TuiTextAttributes.Dim) },
                    TuiLength.Auto)
                .Add(widget, TuiLength.Star());
}

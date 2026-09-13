using Tosh.Runtime;
using Tosh.Tui.Widgets;

namespace Tosh.Tui.Declarative;

/// <summary>
/// Finds and applies the properties a screen re-reads from a script function.
/// </summary>
/// <remarks>
/// A binding lives on the widget — <c>TextSource</c>, <c>ItemsSource</c> and their
/// siblings — rather than in a list kept beside the tree. That is what lets a tree built
/// in code bind exactly like one built from a record: there is one place a binding can
/// be, so there is one thing to look for.
/// </remarks>
public static class TuiBindings
{
    /// <summary>Attaches a source to whichever property of a widget takes one.</summary>
    public static void Attach(TuiWidget widget, IShellCallable source)
    {
        ArgumentNullException.ThrowIfNull(widget);
        ArgumentNullException.ThrowIfNull(source);

        switch (widget)
        {
            case TuiTextWidget text: text.TextSource = source; return;
            case TuiField labelled: labelled.TextSource = source; return;
            case TuiTextField field: field.ValueSource = source; return;
            case TuiBorder border: border.TitleSource = source; return;
            case TuiList list: list.ItemsSource = source; return;
            case TuiScroll { Child: TuiList scrolled }: scrolled.ItemsSource = source; return;
        }

        // A labelled field is a row around an input; the input is what was meant.
        foreach (var child in widget.Children)
        {
            if (child is TuiTextField nested)
            {
                nested.ValueSource = source;
                return;
            }
        }
    }

    /// <summary>Re-reads every bound property in a tree.</summary>
    /// <param name="root">The tree to refresh.</param>
    /// <param name="invoke">Calls a script function with the screen's current values.</param>
    /// <param name="values">What to hand each function.</param>
    public static void Apply(TuiWidget root, Func<IShellCallable, object?, object?> invoke, object? values)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(invoke);

        switch (root)
        {
            case TuiTextWidget { TextSource: { } source } text:
                text.Text = invoke(source, values)?.ToString() ?? string.Empty;
                break;

            case TuiTextField { ValueSource: { } source } field:
                field.Text = invoke(source, values)?.ToString() ?? string.Empty;
                break;

            case TuiBorder { TitleSource: { } source } border:
                border.Title = invoke(source, values)?.ToString();
                break;

            case TuiList { ItemsSource: { } source } list:
                list.Items = AsItems(invoke(source, values));
                break;
        }

        foreach (var child in root.Children)
        {
            Apply(child, invoke, values);
        }
    }

    private static IReadOnlyList<object?> AsItems(object? value)
        => value switch
        {
            null => [],
            string text => [text],
            System.Collections.IEnumerable sequence => sequence.Cast<object?>().ToArray(),
            _ => [value],
        };
}

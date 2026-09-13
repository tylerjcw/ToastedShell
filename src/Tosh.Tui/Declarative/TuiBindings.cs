using Tosh.Runtime;
using Tosh.Tui.Rendering;
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
            case TuiSparkline spark: spark.ValuesSource = source; return;
            case TuiGauge gauge: gauge.AmountSource = source; return;
            case TuiBars bars: bars.BarsSource = source; return;
            case TuiTable table: table.RowsSource = source; return;
            case TuiLines lines: lines.LinesSource = source; return;
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

    /// <summary>
    /// Asks only the visibility predicates, before anything has been drawn.
    /// </summary>
    /// <remarks>
    /// A widget hidden by <c>When</c> is visible until its predicate has been asked, so
    /// the keyboard would otherwise be seated inside a dialog that was never up. Only
    /// these are asked, rather than every binding: the rest are answers about what to
    /// draw, and nothing has been drawn yet.
    /// </remarks>
    public static void ApplyVisibility(
        TuiWidget root,
        Func<IShellCallable, object?, object?> invoke,
        object? values)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(invoke);

        if (root.VisibleSource is { } visible)
        {
            root.IsVisible = invoke(visible, values) is not (null or false or 0);
        }

        foreach (var child in root.Children)
        {
            ApplyVisibility(child, invoke, values);
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

        // Asked first and of every widget, because whether something is drawn at all
        // decides whether anything else about it matters.
        if (root.VisibleSource is { } visible)
        {
            root.IsVisible = invoke(visible, values) is not (null or false or 0);
        }

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
                return;

            case TuiTable { RowsSource: { } source } table:
                table.Rows = AsItems(invoke(source, values));
                return;

            case TuiSparkline { ValuesSource: { } source } spark:
                spark.Values = [.. AsItems(invoke(source, values))
                    .Select(item => TypeConversion.TryConvert(item, typeof(double), out var number)
                        ? (double)number!
                        : 0d)];
                return;

            case TuiBars { BarsSource: { } source } bars:
                bars.Bars = [.. AsItems(invoke(source, values))
                    .Select(item =>
                        ShellRecordUtilities.TryGetValue(item, "Label", out var label) &&
                        ShellRecordUtilities.TryGetValue(item, "Value", out var amount) &&
                        TypeConversion.TryConvert(amount, typeof(double), out var number)
                            ? new TuiBar(label?.ToString() ?? string.Empty, (double)number!)
                            : new TuiBar(item?.ToString() ?? string.Empty, 0))];
                return;

            case TuiGauge { AmountSource: { } source } gauge:
                gauge.Amount = TypeConversion.TryConvert(invoke(source, values), typeof(double), out var amount)
                    ? (double)amount!
                    : 0d;
                return;

            case TuiLines { LinesSource: { } source } lines:
                // Plain text or lines already built as spans: a script writing a live log
                // returns strings, and one that wants colour returns what it drew.
                lines.Lines = invoke(source, values) is IEnumerable<TuiSpanLine> spans
                    ? [.. spans]
                    : [.. AsItems(invoke(source, values))
                        .Select(line => new TuiSpanLine([new TuiSpan(line?.ToString() ?? string.Empty)]))];
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

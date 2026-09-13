using Tosh.Runtime;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// Gives a TUI table the columns the shell would print for the same object.
/// </summary>
/// <remarks>
/// Reflection over a <see cref="System.IO.FileInfo"/> yields forty-nine properties. `ls`
/// shows four, because the display engine has already made that decision — which columns
/// matter, how wide, which way they align — and it is the decision a reader has already
/// learned. `ls | tui table` showing something else would be a second opinion nobody asked
/// for (<c>TUI-0017</c>).
/// </remarks>
internal static class DisplayEngineColumns
{
    /// <summary>Installs the shell's column choice as the table's default.</summary>
    public static void Install(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var engine = new DisplayEngine(runtime.Formatter);

        TuiTable.ColumnSource = row =>
        {
            var options = new DisplayRenderOptions(runtime.Display.Style);

            try
            {
                return engine.TryBuildStreamingColumns(row, options, out var columns)
                    ? [.. columns.Select(column => Convert(engine, options, column))]
                    : null;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Deriving columns is a convenience. A row the display engine cannot read
                // falls back to reflection rather than taking the screen down with it.
                return null;
            }
        };
    }

    private static TuiColumn Convert(DisplayEngine engine, DisplayRenderOptions options, DisplayTableColumn column)
        => new(column.Header)
        {
            // Formatted through the engine, not merely read: a size reads "133 kB" and a
            // date "62 minutes ago" at the prompt, and a table beside it must agree.
            Selector = row => row is null
                ? null
                : engine.FormatTableCellValue(column.ValueAccessor(row), options),
            Align = column.Alignment == DisplayTableAlignment.Right ? TuiAlignment.Right : TuiAlignment.Left,
            Width = TuiLength.Auto.AtMost(column.MaxWidth),
        };
}

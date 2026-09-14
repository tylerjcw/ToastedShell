using Tosh.Runtime;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Cli.Tui;

/// <summary>
/// Teaches the widgets what this shell's answers are.
/// </summary>
/// <remarks>
/// <para>
/// The widgets know nothing about a shell — they are a library, and a script or a C\#
/// program can use them without one. So the decisions a shell has already made, and that
/// a reader has already learned, are installed rather than assumed.
/// </para>
/// <para>
/// Reflection over a <see cref="System.IO.FileInfo"/> yields forty-nine properties; `ls`
/// shows four, because the display engine decided which matter, how wide and which way
/// they align. A table showing something else would be a second opinion nobody asked for
/// (<c>TUI-0017</c>). A tree drawing guides the user turned off in their theme is the same
/// mistake (<c>TUI-0018</c>).
/// </para>
/// </remarks>
internal static class ShellWidgetDefaults
{
    /// <summary>Installs the shell's own answers as the widgets' defaults.</summary>
    public static void Install(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        // Asked on every draw rather than read once, so a theme changed while a screen is
        // up takes effect on the next frame.
        TuiTree.DefaultGlyphs = () => runtime.Config.Theme.Tui.TreeStyle switch
        {
            ToshTuiTreeStyle.Clean => TuiTreeGlyphs.Clean,
            _ => TuiTreeGlyphs.Default,
        };

        // Previews come from whatever the reader already has installed, so a format nobody
        // here has heard of works the day they install something that reads it.
        TuiImage.Loader = ShellImageLoader.Load;

        // Asked once. The environment is what a terminal says about itself, and a reader
        // whose terminal lies sets TOSH_TUI_GRAPHICS rather than being guessed around.
        TuiImage.Protocol = TuiGraphics.Detect(Environment.GetEnvironmentVariable);

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

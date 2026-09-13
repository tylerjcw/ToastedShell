using BenchmarkDotNet.Attributes;
using Tosh.Cli.Tui;
using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Benchmarks;

/// <summary>
/// The baseline for <c>TUI-0001</c>: what it costs to build one frame today, before
/// frames stop being strings.
/// </summary>
/// <remarks>
/// <para>
/// Taken before the rewrite on purpose. Once string frames are gone there is nothing
/// left to compare against, and the number that justifies the change would have been
/// thrown away.
/// </para>
/// <para>
/// These measure frame *construction* only — the cost of turning screen state into
/// output. What the runtime then does with it is a separate and currently larger cost:
/// <c>TuiApplication</c> writes a clear-screen escape followed by the whole frame on
/// every render, so the bytes reported by <see cref="FrameBytes"/> go to the terminal
/// each time, whether or not anything changed.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class TuiRenderBenchmarks
{
    private ToshRuntime _runtime = null!;
    private HelpBrowserScreen _help = null!;
    private HelpBrowserScreen _clr = null!;
    private ConfigBrowserScreen _config = null!;
    private ConfigBrowserScreen _configPrompt = null!;
    private string _longLine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _runtime = ToshRuntime.CreateDefault();
        _help = new HelpBrowserScreen(_runtime, new HelpBrowseRequest(null, null));
        _clr = new HelpBrowserScreen(
            _runtime,
            new HelpBrowseRequest("System.Text.StringBuilder", "System.Text.StringBuilder"));
        _config = new ConfigBrowserScreen(_runtime, new ConfigBrowseRequest(null, null));
        _configPrompt = new ConfigBrowserScreen(_runtime, new ConfigBrowseRequest(null, "Prompt"));

        // Long enough to show the shape of ClipPlain's cost rather than just its constant.
        _longLine = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}"));
    }

    [Benchmark(Description = "help browser, 80x24")]
    public int HelpSmall() => _help.Render(new TuiSize(80, 24)).Content.Length;

    [Benchmark(Description = "help browser, 120x40")]
    public int HelpMedium() => _help.Render(new TuiSize(120, 40)).Content.Length;

    [Benchmark(Description = "help browser, 200x60")]
    public int HelpLarge() => _help.Render(new TuiSize(200, 60)).Content.Length;

    /// <summary>A CLR type page: the heaviest detail pane the help browser draws.</summary>
    [Benchmark(Description = "CLR type page, 120x40")]
    public int ClrTypePage() => _clr.Render(new TuiSize(120, 40)).Content.Length;

    /// <summary>
    /// The config browser draws a live preview of the user's theme and prompt, so its
    /// detail pane does more work per frame than the help browser's.
    /// </summary>
    [Benchmark(Description = "config browser, 120x40")]
    public int ConfigMedium() => _config.Render(new TuiSize(120, 40)).Content.Length;

    /// <summary>
    /// Clipping one long line to a pane width.
    /// </summary>
    /// <remarks>
    /// Called once per line per frame. It appends a character at a time and recomputes
    /// the visible length of the whole accumulated string on each iteration, so the cost
    /// grows with the square of the line length rather than with the width it clips to.
    /// </remarks>
    /// <summary>
    /// The config browser on its most expensive node.
    /// </summary>
    /// <remarks>
    /// The default selection lands on the Theme group and does not draw a prompt preview,
    /// so it hid the worst case entirely: selecting <c>Prompt</c> renders two complete
    /// sample prompts per frame, which runs the prompt's own modules including the one
    /// that reads git state. A benchmark that never selects it cannot notice
    /// (<c>TUI-0014</c>).
    /// </remarks>
    [Benchmark(Description = "config browser on Prompt, 120x40")]
    public int ConfigPromptNode() => _configPrompt.Render(new TuiSize(120, 40)).Content.Length;

    [Benchmark(Description = "ClipPlain, 1200 chars to 80")]
    public string ClipLongLine() => TuiRenderHelpers.ClipPlain(_longLine, 80);

    /// <summary>
    /// Not a timing: the bytes a single frame writes to the terminal, reported so the
    /// diffing renderer has a figure to beat.
    /// </summary>
    [Benchmark(Description = "frame bytes, 120x40 (size, not time)")]
    public int FrameBytes() => _help.Render(new TuiSize(120, 40)).Content.Length;
}

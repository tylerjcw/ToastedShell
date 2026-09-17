using BenchmarkDotNet.Attributes;
using Tosh.Cli.Tui;
using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Language;
using Tosh.Tui.Declarative;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Benchmarks;

/// <summary>
/// What a frame costs to build, and what it costs to send (<c>TUI-0012</c>).
/// </summary>
/// <remarks>
/// <para>
/// Begun as the baseline for <c>TUI-0001</c>, taken before frames stopped being strings —
/// once they were gone there would have been nothing left to compare against. Both halves
/// are now measured against that baseline rather than against a feeling.
/// </para>
/// <para>
/// The timings measure frame <em>construction</em>: turning screen state into cells. The
/// three <c>bytes</c> cases measure what then reaches the terminal, and are sizes rather
/// than times — they are the ones that say whether the diff is doing anything, and the
/// interesting comparison is between them and not against the clock.
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
    private TuiDeclarativeScreen _tree = null!;
    private TuiDeclarativeScreen _bound = null!;
    private TuiDeclarativeScreen _chart = null!;
    private Func<IShellCallable, object?, object?> _invoke = null!;
    private IShellCallable _callable = null!;
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

        // The real invoker, taken the way a script gets one: `tui run` hands it to the
        // request. Building a stand-in here would measure the stand-in.
        var engine = new ToshEngine(_runtime.Language) { IsInteractiveSession = true };
        var produced = engine
            .ExecuteToListAsync("func Row() => \"a row of text\"\ntui run {| Text = &Row |}")
            .GetAwaiter()
            .GetResult();

        var request = (TuiTreeRunRequest)produced[0];

        _invoke = request.Invoke!;
        _callable = (IShellCallable)((IDictionary<string, object?>)request.Node)["Text"]!;

        _tree = Screen(Rows(bind: null));
        _bound = Screen(Rows(bind: _callable));
        _chart = Screen(new TuiChart(Enumerable.Range(0, 2000).Select(value => (double)(value % 97))));

        TuiDeclarativeScreen Screen(TuiWidget root)
            => new(root, [], _invoke, title: "bench", refreshInterval: null);

        // Twenty rows, which is a realistic pane and twenty binding calls per frame when
        // they are bound.
        TuiWidget Rows(IShellCallable? bind)
        {
            var stack = new TuiStack(TuiOrientation.Vertical);

            for (var row = 0; row < 20; row += 1)
            {
                var text = new TuiTextWidget($"row {row} of a screen built from markup");

                if (bind is not null)
                {
                    text.TextSource = bind;
                }

                stack.Items = [.. stack.Items, text];
            }

            return new TuiBorder(stack) { Title = "rows" };
        }
    }

    [Benchmark(Description = "help browser, 80x24")]
    public int HelpSmall() => _help.Render(new TuiSize(80, 24)).Buffer.Height;

    [Benchmark(Description = "help browser, 120x40")]
    public int HelpMedium() => _help.Render(new TuiSize(120, 40)).Buffer.Height;

    [Benchmark(Description = "help browser, 200x60")]
    public int HelpLarge() => _help.Render(new TuiSize(200, 60)).Buffer.Height;

    /// <summary>A CLR type page: the heaviest detail pane the help browser draws.</summary>
    [Benchmark(Description = "CLR type page, 120x40")]
    public int ClrTypePage() => _clr.Render(new TuiSize(120, 40)).Buffer.Height;

    /// <summary>
    /// The config browser draws a live preview of the user's theme and prompt, so its
    /// detail pane does more work per frame than the help browser's.
    /// </summary>
    [Benchmark(Description = "config browser, 120x40")]
    public int ConfigMedium() => _config.Render(new TuiSize(120, 40)).Buffer.Height;

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
    public int ConfigPromptNode() => _configPrompt.Render(new TuiSize(120, 40)).Buffer.Height;

    /// <summary>
    /// Clipping one long line to a pane width, called once per line per frame.
    /// </summary>
    /// <remarks>
    /// It used to append one character at a time and recompute the visible length of the
    /// whole accumulated string on each pass, so the cost grew with the square of the width
    /// it clipped to. It walks the text once now (<c>TUI-0012</c>).
    /// </remarks>
    [Benchmark(Description = "ClipPlain, 1200 chars to 80")]
    public string ClipLongLine() => TuiRenderHelpers.ClipPlain(_longLine, 80);

    /// <summary>
    /// Not a timing: the bytes a single frame writes to the terminal.
    /// </summary>
    /// <remarks>
    /// This was the figure the diffing renderer had to beat, measured back when a frame
    /// was a string and every one of them was a full repaint. It is now what the renderer
    /// actually emits for a first frame, and <see cref="FrameBytesUnchanged"/> beside it is
    /// what it emits for a frame where nothing moved — which is the comparison that says
    /// whether the diff is doing anything.
    /// </remarks>
    [Benchmark(Description = "frame bytes, 120x40 (size, not time)")]
    public int FrameBytes()
        => TuiTerminalWriter.Present(_help.Render(new TuiSize(120, 40)).Buffer).Length;

    /// <summary>The bytes a frame writes when nothing on it changed.</summary>
    [Benchmark(Description = "frame bytes unchanged, 120x40 (size, not time)")]
    public int FrameBytesUnchanged()
    {
        var previous = _help.Render(new TuiSize(120, 40)).Buffer;

        return TuiTerminalWriter.Present(previous, _help.Render(new TuiSize(120, 40)).Buffer).Length;
    }

    /// <summary>
    /// The bytes a frame writes when one row changed.
    /// </summary>
    /// <remarks>
    /// The budget line the diff exists for: a clock ticking in a corner should write the
    /// corner. Compared against <see cref="FrameBytes"/>, which is the same frame sent in
    /// full — the ratio is the whole claim.
    /// </remarks>
    [Benchmark(Description = "frame bytes, one row changed, 120x40 (size, not time)")]
    public int FrameBytesOneRow()
    {
        var previous = _help.Render(new TuiSize(120, 40)).Buffer;
        var next = _help.Render(new TuiSize(120, 40)).Buffer;

        next.DrawText(0, 20, "one row of this frame is different now", default);

        return TuiTerminalWriter.Present(previous, next).Length;
    }

    /// <summary>
    /// A screen built from markup rather than hand-rendered.
    /// </summary>
    /// <remarks>
    /// The two browsers are the old way and are what the baseline measured. Everything a
    /// script writes goes through the widget tree instead, so the path that matters for new
    /// work was not being measured at all.
    /// </remarks>
    [Benchmark(Description = "widget tree, 120x40")]
    public int WidgetTree() => _tree.Render(new TuiSize(120, 40)).Buffer.Height;

    /// <summary>The same tree with its bindings re-asked, which is what a redraw does.</summary>
    /// <remarks>
    /// A pull binding is a script function called once per frame per bound property, and
    /// that cost sits on the frame path rather than beside it. The decision to leave it
    /// there was made on reasoning; this is the measurement it should have had.
    /// </remarks>
    [Benchmark(Description = "widget tree with bindings, 120x40")]
    public int WidgetTreeBound() => _bound.Render(new TuiSize(120, 40)).Buffer.Height;

    /// <summary>One call into a script function through the binding path.</summary>
    /// <remarks>
    /// Isolated from the frame, because a screen with twenty bound properties pays this
    /// twenty times per redraw and the frame time alone does not say which half is which.
    /// </remarks>
    [Benchmark(Description = "one binding call")]
    public object? BindingCall() => _invoke(_callable, null);

    /// <summary>A chart, which is the most arithmetic any one widget does per frame.</summary>
    [Benchmark(Description = "chart with 2000 samples, 120x40")]
    public int Chart() => _chart.Render(new TuiSize(120, 40)).Buffer.Height;
}

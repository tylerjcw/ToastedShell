using System.Dynamic;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Every widget is reachable from markup, and every widget markup names exists.
/// </summary>
/// <remarks>
/// <para>
/// Markup is a first-class way to write a screen rather than a reduced one, so a widget
/// that only the object style can reach is a gap. Two of them had opened up — a document
/// pane and a scroll container — because a widget lands in <c>Widgets/</c> and registering
/// it is a separate step nothing was watching.
/// </para>
/// <para>
/// This watches it. The registry is compared against the widget types themselves, so
/// adding a widget without a name fails here rather than being discovered by someone
/// writing markup against it.
/// </para>
/// </remarks>
public sealed class TuiMarkupSurfaceTests
{
    /// <summary>Widgets a markup tree has no business naming, and why.</summary>
    private static readonly Dictionary<string, string> NotNamed = new(StringComparer.Ordinal)
    {
        ["TuiBorder"] = "written as `Box`, which is what a titled border is called in markup",
        ["TuiTextField"] = "written as `Field`, which is the labelled row anyone actually wants",
        ["TuiStack"] = "written as `Row` or `Column`, which say which way it runs",
    };

    private static IDictionary<string, object?> Node(params (string Key, object? Value)[] fields)
    {
        var record = new ExpandoObject();
        var dictionary = (IDictionary<string, object?>)record;

        foreach (var (key, value) in fields)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    public static TheoryData<string, object?> EveryName()
    {
        var data = new TheoryData<string, object?>();

        // The primary value each name expects: children, items, a number, or text.
        foreach (var (name, primary) in ((string, object?)[])
                 [
                     ("Text", "hello"),
                     ("Lines", new object?[] { "one", "two" }),
                     ("List", new object?[] { "a", "b" }),
                     ("Table", new object?[] { "a", "b" }),
                     ("Tree", "root"),
                     ("Field", "Name"),
                     ("Button", "OK"),
                     ("Gauge", null),
                     ("Spark", new object?[] { 1, 2, 3 }),
                     ("Bars", new object?[] { Node(("Label", "a"), ("Value", 1)) }),
                     ("Row", new object?[] { "a" }),
                     ("Column", new object?[] { "a" }),
                     ("Box", new object?[] { "a" }),
                     ("Scroll", new object?[] { "a" }),
                     ("Form", new object?[] { "a" }),
                     ("Overlay", new object?[] { "a" }),
                 ])
        {
            data.Add(name, primary);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryName))]
    public void Every_registered_name_builds_and_draws(string name, object? primary)
    {
        var widget = TuiTreeBuilder.Build(Node((name, primary)));

        var buffer = new TuiBuffer(new TuiSize(30, 8));
        var bounds = new TuiRect(0, 0, 30, 8);

        widget.Measure(TuiConstraints.From(new TuiSize(30, 8)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));
    }

    [Fact]
    public void Every_widget_has_a_name_in_markup_or_a_reason_not_to()
    {
        var widgets = typeof(TuiWidget).Assembly
            .GetTypes()
            .Where(type => type.IsPublic && !type.IsAbstract && typeof(TuiWidget).IsAssignableFrom(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var registered = TuiWidgetRegistry.CreateDefault().Names;

        // A markup name is the readable half of the type name — `spark` for `TuiSparkline`,
        // `text` for `TuiTextWidget` — so the match is a prefix rather than an equality.
        var missing = widgets
            .Where(widget => !NotNamed.ContainsKey(widget))
            .Where(widget => !registered.Any(name =>
                widget.StartsWith($"Tui{name}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"These widgets cannot be written as markup: {string.Join(", ", missing)}. "
            + "Register a name in TuiWidgetRegistry.CreateDefault, or record in NotNamed why markup "
            + "should not have one.");
    }

    [Fact]
    public void A_name_that_is_registered_twice_would_be_caught()
    {
        // The registry replaces by name, so a second registration is silent. Counting them
        // is how a duplicate turns into a failure rather than into whichever won.
        var names = TuiWidgetRegistry.CreateDefault().Names;

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The guide's markup reference lists every name the registry knows.
    /// </summary>
    /// <remarks>
    /// Documentation written beside a table drifts from it; documentation checked against
    /// it cannot. This is the same rule the shortcut footer follows — a help line that can
    /// claim a key which does not work is worse than no help line.
    /// </remarks>
    [Fact]
    public void The_guide_documents_every_markup_name()
    {
        var guide = Path.Combine(ToshCli.RepositoryRoot, "docs", "spec", "tui-guide.tex");

        Assert.True(File.Exists(guide), $"The TUI guide is missing: {guide}");

        var text = File.ReadAllText(guide);

        var undocumented = TuiWidgetRegistry.CreateDefault().Names
            .Select(name => $"{char.ToUpperInvariant(name[0])}{name[1..]}")
            // Case-insensitively, because markup is: the guide writes `MenuBar` where the
            // registry holds `menubar`, and both are the name the reader types.
            .Where(name => !text.Contains($"\\texttt{{{name}}}", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains($"\n{name}    ", StringComparison.OrdinalIgnoreCase)
                        && !text.Contains($"{{| {name} ", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            $"These markup names are not in docs/spec/tui-guide.tex: {string.Join(", ", undocumented)}.");
    }

    [Fact]
    public void Keys_written_on_a_node_are_registered_on_that_widget()
    {
        var pressed = 0;

        var widget = TuiTreeBuilder.Build(
            Node(
                ("Column", new object?[] { "a" }),
                ("Keys", new object?[]
                {
                    Node(("Key", "t"), ("Do", new Pressed(() => pressed += 1))),
                })),
            registry: null,
            invoke: (callable, _) => ((Pressed)callable).Fire(),
            out _);

        Assert.NotNull(widget.Keys);
        Assert.True(widget.Keys!.TryHandle(new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false), out _));
        Assert.Equal(1, pressed);
    }

    private sealed class Pressed(Action action) : Tosh.Runtime.IShellCallable
    {
        public object? Fire()
        {
            action();
            return null;
        }

        public string CallableName => "pressed";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 0;

        public IAsyncEnumerable<object?> InvokeAsync(Tosh.Runtime.CommandContext context)
            => throw new NotSupportedException();
    }

    [Fact]
    public void A_document_keeps_the_lines_it_was_given()
    {
        var widget = TuiTreeBuilder.Build(Node(("Lines", new object?[] { "first", "second" })));

        var lines = Assert.IsType<TuiLines>(widget);

        Assert.Equal(["first", "second"], lines.Lines.Select(line => line.Text));
    }

    [Fact]
    public void A_button_that_exits_ends_the_screen_as_well_as_running_its_handler()
    {
        // Markup has no handle on its own form, so a dialog written this way could raise
        // itself and never let the reader out.
        var pressed = 0;

        var widget = TuiTreeBuilder.Build(
            Node(("Button", "Discard"), ("Exit", true), ("OnPress", new Pressed(() => pressed += 1))),
            registry: null,
            invoke: (callable, _) => ((Pressed)callable).Fire(),
            out _);

        var button = Assert.IsType<TuiButton>(widget);

        Assert.False(button.ClosesScreen);

        button.Pressed!.Invoke();

        Assert.Equal(1, pressed);
        Assert.True(button.ClosesScreen);
    }

    [Fact]
    public void A_source_written_on_a_node_is_registered_on_that_widget()
    {
        var arrived = new List<object?>();

        var widget = TuiTreeBuilder.Build(
            Node(
                ("Lines", new object?[] { "waiting" }),
                ("Feed", Node(("Source", new object?[] { 1, 2 }), ("Do", new Pressed(() => arrived.Add(1)))))),
            registry: null,
            invoke: (callable, _) => ((Pressed)callable).Fire(),
            out _);

        var feeds = Assert.IsType<TuiFeeds>(widget.Feeds);
        var feed = Assert.Single(feeds.Sources);

        Assert.Equal(new object?[] { 1, 2 }, feed.Source);

        feed.Arrived(null);

        Assert.Single(arrived);
    }

    [Fact]
    public void A_node_with_no_source_carries_none()
    {
        Assert.Null(TuiTreeBuilder.Build(Node(("Text", "plain"))).Feeds);
    }

    [Fact]
    public void A_scroll_wraps_what_it_is_given()
    {
        var widget = TuiTreeBuilder.Build(Node(("Scroll", new object?[] { "inside" })));

        Assert.IsType<TuiTextWidget>(Assert.IsType<TuiScroll>(widget).Child);
    }
}

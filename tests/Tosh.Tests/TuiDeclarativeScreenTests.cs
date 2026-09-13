using System.Dynamic;
using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Pull bindings and handlers: the parts of a declarative screen that call back into a
/// script.
/// </summary>
/// <remarks>
/// The surface syntax these are written in is still being settled. None of it is under
/// test here — a tree is built directly — because the tree is what every candidate
/// syntax produces, and these behaviours belong to the tree rather than to the spelling.
/// </remarks>
public sealed class TuiDeclarativeScreenTests
{
    /// <summary>
    /// Stands in for a script function.
    /// </summary>
    /// <remarks>
    /// It is never invoked through <see cref="InvokeAsync"/>: the screen calls script
    /// functions through an invoker supplied by whoever has a command context, and these
    /// tests supply one that recognises this token. That is the same seam the real
    /// runtime uses, so the fake is a token rather than an implementation.
    /// </remarks>
    private sealed class Handle(string name) : IShellCallable
    {
        public string CallableName => name;

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 1;

        public IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
            => throw new NotSupportedException("The screen calls through the supplied invoker.");
    }

    private static IDictionary<string, object?> Node(params (string Key, object? Value)[] fields)
    {
        var dictionary = (IDictionary<string, object?>)new ExpandoObject();

        foreach (var (key, value) in fields)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    private static string[] Draw(TuiDeclarativeScreen screen, int width = 30, int height = 4)
    {
        var frame = screen.Render(new TuiSize(width, height));
        Assert.NotNull(frame.Buffer);

        return [.. Enumerable.Range(0, height).Select(row => frame.Buffer.RowText(row).TrimEnd())];
    }

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    [Fact]
    public void A_bound_property_is_re_read_on_every_redraw()
    {
        var reads = 0;
        var clock = new Handle("clock");

        var root = TuiTreeBuilder.Build(
            Node(("Text", clock)),
            null,
            (callable, _) => ReferenceEquals(callable, clock) ? $"read {++reads}" : null,
            out var bindings);

        var screen = new TuiDeclarativeScreen(root, bindings, (callable, argument) =>
            ReferenceEquals(callable, clock) ? $"read {++reads}" : null);

        Assert.Equal("read 1", Draw(screen)[0]);
        Assert.Equal("read 2", Draw(screen)[0]);
        Assert.Equal("read 3", Draw(screen)[0]);
    }

    [Fact]
    public void A_binding_is_handed_the_current_values_of_the_form()
    {
        var summary = new Handle("summary");

        var root = TuiTreeBuilder.Build(
            Node(("Column", new object?[]
            {
                Node(("Field", "Width"), ("Id", "Width"), ("Value", "7")),
                Node(("Text", summary)),
            })),
            null,
            null,
            out var bindings);

        object? seen = null;

        var screen = new TuiDeclarativeScreen(root, bindings, (_, argument) =>
        {
            seen = argument;
            return "ok";
        });

        Draw(screen);

        // The binding is a function of the screen's state, so the state is the argument.
        Assert.NotNull(seen);
        Assert.True(ShellRecordUtilities.TryGetValue(seen, "Width", out var width));
        Assert.Equal("7", width);
    }

    [Fact]
    public void A_handler_fires_when_something_happens_rather_than_when_something_is_drawn()
    {
        var onSelect = new Handle("onSelect");
        var calls = new List<object?>();

        var root = TuiTreeBuilder.Build(
            Node(("List", new object?[] { "alpha", "beta" }), ("Id", "items"), ("OnSelect", onSelect)),
            null,
            (callable, argument) =>
            {
                calls.Add(argument);
                return null;
            },
            out var bindings);

        var screen = new TuiDeclarativeScreen(root, bindings);

        // Drawing calls no handler: a handler is not a binding.
        Draw(screen);
        Assert.Empty(calls);

        screen.HandleInput(Key(ConsoleKey.DownArrow));
        screen.HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal(["beta"], calls);
    }

    [Fact]
    public void Values_are_reported_by_the_id_each_widget_was_given()
    {
        var root = TuiTreeBuilder.Build(Node(("Column", new object?[]
        {
            Node(("Field", "Wide"), ("Id", "Width"), ("Value", "12")),
            Node(("List", new object?[] { "Nuclear", "MOX" }), ("Id", "Kind")),
            Node(("Text", "not named")),
        })));

        var screen = new TuiDeclarativeScreen(root, []);
        var values = screen.Values();

        Assert.Equal("12", values["Width"]);
        Assert.Equal("Nuclear", values["Kind"]);

        // Widgets nobody named are not in the result; most of a tree is scaffolding.
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void Enter_submits_the_values_and_escape_cancels()
    {
        var root = TuiTreeBuilder.Build(Node(("Field", "Wide"), ("Id", "Width"), ("Value", "3")));

        var submitted = new TuiDeclarativeScreen(root, []);
        Assert.Equal(TuiScreenResult.Exit, submitted.HandleInput(Key(ConsoleKey.Enter)));
        Assert.False(submitted.Outcome!.Cancelled);
        Assert.Equal("3", submitted.Outcome.Values["Width"]);

        var cancelled = new TuiDeclarativeScreen(
            TuiTreeBuilder.Build(Node(("Field", "Wide"), ("Id", "Width"))), []);

        Assert.Equal(TuiScreenResult.Exit, cancelled.HandleInput(Key(ConsoleKey.Escape)));
        Assert.True(cancelled.Outcome!.Cancelled);
    }

    [Fact]
    public void Tab_moves_between_the_things_that_can_hold_the_keyboard()
    {
        var root = TuiTreeBuilder.Build(Node(("Column", new object?[]
        {
            Node(("Field", "One"), ("Id", "one")),
            Node(("Field", "Two"), ("Id", "two")),
        })));

        var screen = new TuiDeclarativeScreen(root, []);
        Draw(screen);

        screen.HandleInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false)));
        screen.HandleInput(Key(ConsoleKey.Tab));
        screen.HandleInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('y', ConsoleKey.Y, false, false, false)));

        var values = screen.Values();
        Assert.Equal("x", values["one"]);
        Assert.Equal("y", values["two"]);
    }

    [Fact]
    public void A_screen_with_no_invoker_leaves_its_bindings_inert_rather_than_failing()
    {
        var root = TuiTreeBuilder.Build(Node(("Text", new Handle("unused"))), null, null, out var bindings);

        // A tree built without a way to call back — in a test, or a headless host — draws
        // rather than throwing.
        var screen = new TuiDeclarativeScreen(root, bindings);

        Assert.Equal(string.Empty, Draw(screen)[0]);
    }
}

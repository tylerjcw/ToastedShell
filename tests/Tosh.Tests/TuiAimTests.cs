using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A key that aims at part of the screen rather than at a function (<c>TUI-0026</c>).
/// </summary>
public sealed class TuiAimTests
{
    private static TuiInputEvent Key(ConsoleKeyInfo key) => TuiInputEvent.FromKey(key);

    private static TuiInputEvent Type(char character)
        => Key(new ConsoleKeyInfo(character, default, false, false, false));

    private static TuiInputEvent Named(ConsoleKey key)
        => Key(new ConsoleKeyInfo('\0', key, false, false, false));

    [Fact]
    public void A_key_can_move_the_keyboard_to_a_widget_by_id()
    {
        // A list, not a text field, because `/` is a printable character and a field would
        // type it. Which is the routing working: a screen key never takes a character out
        // from under someone who is typing.
        var body = new TuiList(["one", "two"]) { Id = "body" };
        var query = new TuiTextField { Id = "query" };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [body, query] };

        root.Keys = new TuiShortcuts();
        root.Keys.Focus("/", "/", "search", "query");

        using var screen = new TuiDeclarativeScreen(root, []);
        screen.Render(new TuiSize(30, 4));

        Assert.True(body.IsFocused);

        screen.HandleInput(Type('/'));

        Assert.True(query.IsFocused);
        Assert.False(body.IsFocused);
    }

    [Fact]
    public void A_printable_key_still_belongs_to_whatever_is_being_typed_in()
    {
        // The rule that makes screen keys safe, restated for keys that aim: `/` reaches
        // the table only when nothing nearer the keyboard wanted it.
        var body = new TuiTextField { Id = "body" };
        var query = new TuiTextField { Id = "query" };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [body, query] };

        root.Keys = new TuiShortcuts();
        root.Keys.Focus("/", "/", "search", "query");

        using var screen = new TuiDeclarativeScreen(root, []);
        screen.Render(new TuiSize(30, 4));
        screen.HandleInput(Type('/'));

        Assert.True(body.IsFocused);
        Assert.Equal("/", body.Text);
    }

    [Fact]
    public void And_can_hand_it_back_where_it_was()
    {
        var body = new TuiTextField { Id = "body" };
        var query = new TuiTextField { Id = "query" };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [body, query] };

        root.Keys = new TuiShortcuts();
        root.Keys.Focus("f2", "F2", "search", "query");
        root.Keys.Back("f3", "F3", "back");

        using var screen = new TuiDeclarativeScreen(root, []);
        screen.Render(new TuiSize(30, 4));
        screen.HandleInput(Named(ConsoleKey.F2));

        Assert.True(query.IsFocused);

        screen.HandleInput(Named(ConsoleKey.F3));

        Assert.True(body.IsFocused);
    }

    [Fact]
    public void A_key_can_press_a_widget_without_taking_the_keyboard_off_what_you_are_typing_in()
    {
        var pressed = 0;
        var body = new TuiTextField { Id = "body" };
        var refresh = new TuiButton("Refresh", () => pressed += 1) { Id = "refresh" };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [body, refresh] };

        root.Keys = new TuiShortcuts();
        root.Keys.Press("f5", "F5", "refresh", "refresh");

        using var screen = new TuiDeclarativeScreen(root, []);
        screen.Render(new TuiSize(30, 4));
        screen.HandleInput(Named(ConsoleKey.F5));

        Assert.Equal(1, pressed);
        Assert.True(body.IsFocused);
        Assert.False(refresh.IsFocused);
    }

    [Fact]
    public void Aiming_at_something_hidden_does_nothing()
    {
        var pressed = 0;
        var body = new TuiTextField { Id = "body" };
        var gone = new TuiButton("Gone", () => pressed += 1) { Id = "gone", IsVisible = false };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [body, gone] };

        using var screen = new TuiDeclarativeScreen(root, []);
        screen.Render(new TuiSize(30, 4));

        Assert.False(screen.Focus("gone"));
        Assert.False(screen.Press("gone"));
        Assert.Equal(0, pressed);
        Assert.True(body.IsFocused);
    }

    [Fact]
    public void Aiming_at_a_name_nothing_carries_does_nothing()
    {
        using var screen = new TuiDeclarativeScreen(new TuiTextField { Id = "body" }, []);
        screen.Render(new TuiSize(30, 2));

        Assert.False(screen.Focus("nowhere"));
        Assert.False(screen.Press("nowhere"));
        Assert.False(screen.Restore());
    }

    [Fact]
    public void Aiming_at_something_behind_a_dialog_does_nothing()
    {
        // Reachability is the focus scope's answer, not the tree's: a field under a modal
        // is in the tree and out of reach, and a key that jumped to it would put the caret
        // somewhere the reader cannot see it.
        var body = new TuiTextField { Id = "body" };
        var overlay = new TuiOverlay(body, new TuiButton("OK") { Id = "ok" });

        using var screen = new TuiDeclarativeScreen(overlay, []);
        screen.Render(new TuiSize(30, 6));

        Assert.False(screen.Focus("body"));
        Assert.True(screen.Focus("ok"));
    }

    [Fact]
    public void A_key_written_in_markup_aims_the_same_way()
    {
        var widget = TuiTreeBuilder.Build(new Dictionary<string, object?>
        {
            ["Column"] = new object?[]
            {
                new Dictionary<string, object?> { ["Field"] = "Body", ["Id"] = "body" },
                new Dictionary<string, object?> { ["Field"] = "Find", ["Id"] = "query" },
            },
            ["Keys"] = new object?[]
            {
                new Dictionary<string, object?> { ["Key"] = "f2", ["Focus"] = "query" },
                new Dictionary<string, object?> { ["Key"] = "escape", ["Back"] = true },
            },
        });

        using var screen = new TuiDeclarativeScreen(widget, []);
        screen.Render(new TuiSize(40, 4));

        Assert.Equal(2, widget.Keys!.Registered.Count);

        // The id is on the labelled row and the input inside it is what takes the
        // keyboard, so "which row is the keyboard in" is the question worth asking.
        Assert.Equal("body", Holding(widget));

        screen.HandleInput(Named(ConsoleKey.F2));

        Assert.Equal("query", Holding(widget));

        screen.HandleInput(Named(ConsoleKey.Escape));

        Assert.Equal("body", Holding(widget));

        static string? Holding(TuiWidget widget)
        {
            if (widget.Id is { Length: > 0 } id && Anywhere(widget))
            {
                return id;
            }

            return widget.Children.Select(Holding).FirstOrDefault(found => found is not null);
        }

        static bool Anywhere(TuiWidget widget)
            => widget.IsFocused || widget.Children.Any(Anywhere);
    }

    [Fact]
    public void A_table_nobody_hung_on_a_screen_is_inert_rather_than_fatal()
    {
        var keys = new TuiShortcuts();

        keys.Focus("/", "/", "search", "query");

        Assert.True(keys.TryHandle(new ConsoleKeyInfo('/', default, false, false, false), out var result));
        Assert.Equal(TuiScreenResult.Continue, result);
    }
}

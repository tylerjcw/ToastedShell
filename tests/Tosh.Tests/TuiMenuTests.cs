using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Menus, their bar, and the layer a popup lives on (<c>TUI-0023</c>).
/// </summary>
public sealed class TuiMenuTests
{
    private static TuiInputEvent Named(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static TuiInputEvent Type(char character)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo(character, default, false, false, false));

    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static TuiMenu Sample(Action? open = null)
        => new("File", [
            new TuiMenuItem("New", "Ctrl+N"),
            new TuiMenuItem("Open", "Ctrl+O", open),
            TuiMenuItem.Separator,
            new TuiMenuItem("Replace", IsEnabled: false),
            new TuiMenuItem("Quit", "q"),
        ]);

    [Fact]
    public void A_command_shows_its_key_on_the_right()
    {
        var menu = new TuiMenu("File", [new TuiMenuItem("Open", "Ctrl+O")]);

        // Two either side, and at least two between a label and its key so the pair never
        // reads as one string.
        Assert.Equal(14, menu.Measure(TuiConstraints.Unbounded).Width);
        Assert.Equal([" Open  Ctrl+O"], Render(menu, 14, 1));
    }

    [Fact]
    public void The_keyboard_steps_over_separators_and_disabled_commands()
    {
        var menu = Sample();

        new TuiFocus(menu).Focus(menu);

        Assert.Equal(0, menu.Selected);

        menu.OnInput(Named(ConsoleKey.DownArrow));

        Assert.Equal(1, menu.Selected);

        // Past the separator and past "Replace", which is drawn and cannot be landed on.
        menu.OnInput(Named(ConsoleKey.DownArrow));

        Assert.Equal("Quit", menu.SelectedItem?.Label);
    }

    [Fact]
    public void The_command_the_keyboard_is_on_is_marked_without_colour()
    {
        // A band of bold spaces is invisible on a terminal that has no colour, which is the
        // same reason a button draws a marker.
        var menu = Sample();

        new TuiFocus(menu).Focus(menu);

        Assert.StartsWith("▸New", Render(menu, 14, 5)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_runs_the_command_and_says_which()
    {
        var opened = 0;
        var chosen = string.Empty;
        var menu = Sample(() => opened += 1);

        menu.Chosen = item => chosen = item.Label;
        menu.Selected = 1;

        Assert.True(menu.Activate());
        Assert.Equal(1, opened);
        Assert.Equal("Open", chosen);
    }

    [Fact]
    public void A_bar_takes_the_keyboard_only_while_something_is_down()
    {
        // Otherwise it takes it first, being at the top of the tree, and a letter typed at
        // a screen that has just opened opens a menu instead of being typed.
        var bar = new TuiMenuBar([Sample()]);

        Assert.False(bar.IsFocusable);

        bar.OpenAt(0);

        Assert.True(bar.IsFocusable);

        bar.Close();

        Assert.False(bar.IsFocusable);
    }

    [Fact]
    public void Walking_the_bar_takes_the_open_menu_with_it()
    {
        var bar = new TuiMenuBar([new TuiMenu("File"), new TuiMenu("Edit"), new TuiMenu("Help")]);

        bar.OpenAt(0);
        bar.OnInput(Named(ConsoleKey.RightArrow));

        Assert.Equal("Edit", bar.Open?.Label);

        // And wraps, because a bar is a ring once you are walking it.
        bar.OnInput(Named(ConsoleKey.RightArrow));
        bar.OnInput(Named(ConsoleKey.RightArrow));

        Assert.Equal("File", bar.Open?.Label);
    }

    [Fact]
    public void A_letter_opens_the_menu_it_names()
    {
        var bar = new TuiMenuBar([new TuiMenu("File"), new TuiMenu("Edit")]);

        bar.OnInput(Type('e'));

        Assert.Equal("Edit", bar.Open?.Label);
    }

    [Fact]
    public void A_screen_with_a_bar_gets_a_layer_to_put_the_popup_on()
    {
        // The author writes a bar where it belongs — one row at the top of a column — and
        // does not also have to wrap the whole screen in an overlay to make it work.
        var bar = new TuiMenuBar([Sample()]);
        var root = new TuiStack(TuiOrientation.Vertical)
        {
            Items = [bar, new TuiTextWidget("page") { Size = "*" }],
        };

        using var screen = new TuiDeclarativeScreen(root, []);

        Assert.DoesNotContain("New", screen.Render(new TuiSize(30, 8)).ToPlainText(), StringComparison.Ordinal);

        bar.OpenAt(0);

        var open = screen.Render(new TuiSize(30, 8)).ToPlainText();

        Assert.Contains("New", open, StringComparison.Ordinal);
        Assert.Contains("Quit", open, StringComparison.Ordinal);
    }

    [Fact]
    public void The_popup_opens_under_the_name_it_came_out_of()
    {
        var bar = new TuiMenuBar([new TuiMenu("File", [new TuiMenuItem("New")]), Sample()]);
        var root = new TuiStack(TuiOrientation.Vertical)
        {
            Items = [bar, new TuiTextWidget("page") { Size = "*" }],
        };

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(40, 8));
        bar.OpenAt(1);

        var rows = screen.Render(new TuiSize(40, 8)).ToPlainText().Split('\n');
        var marked = rows.First(row => row.Contains('▸', StringComparison.Ordinal));

        // The popup starts under the name it came out of rather than in the middle of the
        // terminal, which is the only information a popup's position carries.
        Assert.True(
            marked.IndexOf('▸', StringComparison.Ordinal) >= bar.Open!.Bounds.Left,
            $"The popup opened at {marked.IndexOf('▸', StringComparison.Ordinal)}, left of its name at {bar.Open.Bounds.Left}.");
    }

    [Fact]
    public void A_popup_that_would_fall_off_the_bottom_opens_upwards()
    {
        var bar = new TuiMenuBar([Sample()]);
        var root = new TuiStack(TuiOrientation.Vertical)
        {
            Items = [new TuiTextWidget("page") { Size = "*" }, bar],
        };

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(30, 8));
        bar.OpenAt(0);

        var rows = screen.Render(new TuiSize(30, 8)).ToPlainText().Split('\n');
        var bottom = Array.FindIndex(rows, row => row.Contains("File", StringComparison.Ordinal));
        var first = Array.FindIndex(rows, row => row.Contains("New", StringComparison.Ordinal));

        // The bar is the last row, so a menu opening under it would have nowhere to go.
        Assert.Equal(rows.Length - 1, bottom);
        Assert.InRange(first, 0, bottom - 1);
    }

    [Fact]
    public void Escape_closes_the_menu_rather_than_the_screen()
    {
        var bar = new TuiMenuBar([Sample()]);
        var page = new TuiTextField("draft") { Size = "*" };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [bar, page] };

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(30, 8));
        bar.OpenAt(0);
        screen.Render(new TuiSize(30, 8));

        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Named(ConsoleKey.Escape)));
        Assert.Null(bar.Open);

        screen.Render(new TuiSize(30, 8));

        // And the keyboard comes back to the page, because the bar stops being focusable
        // the moment nothing is down.
        Assert.True(page.IsFocused);
    }

    [Fact]
    public void Closing_hands_the_keyboard_back_where_it_was()
    {
        // Not to the first field on the screen, which is where revalidation alone would
        // put it: a reader who opened a menu from the third pane expects the third pane.
        var bar = new TuiMenuBar([Sample()]);
        var first = new TuiTextField("one");
        var second = new TuiTextField("two");
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [bar, first, second] };

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(30, 8));
        screen.HandleInput(Named(ConsoleKey.Tab));
        screen.Render(new TuiSize(30, 8));

        Assert.True(second.IsFocused);

        bar.OpenAt(0);
        screen.Render(new TuiSize(30, 8));
        screen.HandleInput(Named(ConsoleKey.Escape));
        screen.Render(new TuiSize(30, 8));

        Assert.True(second.IsFocused);
        Assert.False(first.IsFocused);
    }

    [Fact]
    public void Choosing_from_the_popup_takes_it_down()
    {
        var opened = 0;
        var bar = new TuiMenuBar([Sample(() => opened += 1)]);
        var root = new TuiStack(TuiOrientation.Vertical)
        {
            Items = [bar, new TuiTextWidget("page") { Size = "*" }],
        };

        using var screen = new TuiDeclarativeScreen(root, []);

        screen.Render(new TuiSize(30, 8));
        bar.OpenAt(0);
        screen.Render(new TuiSize(30, 8));

        screen.HandleInput(Named(ConsoleKey.DownArrow));
        screen.HandleInput(Named(ConsoleKey.Enter));

        Assert.Equal(1, opened);
        Assert.Null(bar.Open);
    }

    [Fact]
    public void A_bar_written_in_markup_is_the_same_bar()
    {
        var chosen = 0;

        var widget = TuiTreeBuilder.Build(
            new Dictionary<string, object?>
            {
                ["MenuBar"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Menu"] = "File",
                        ["Items"] = new object?[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["Item"] = "Open",
                                ["Key"] = "Ctrl+O",
                                ["Do"] = new Chose(() => chosen += 1),
                            },
                            new Dictionary<string, object?> { ["Separator"] = true },
                            new Dictionary<string, object?> { ["Item"] = "Gone", ["Enabled"] = false },
                        },
                    },
                },
            },
            registry: null,
            invoke: (callable, _) => ((Chose)callable).Fire(),
            out _);

        var bar = Assert.IsType<TuiMenuBar>(widget);
        var menu = Assert.Single(bar.Menus);

        Assert.Equal("File", menu.Title);
        Assert.Equal(["Open", "", "Gone"], menu.Items.Select(item => item.Label));
        Assert.True(menu.Items[1].IsSeparator);
        Assert.False(menu.Items[2].IsEnabled);

        menu.Selected = 0;
        menu.Activate();

        Assert.Equal(1, chosen);
    }

    private sealed class Chose(Action action) : Tosh.Runtime.IShellCallable
    {
        public object? Fire()
        {
            action();
            return null;
        }

        public string CallableName => "chose";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 0;

        public IAsyncEnumerable<object?> InvokeAsync(Tosh.Runtime.CommandContext context)
            => throw new NotSupportedException();
    }
}

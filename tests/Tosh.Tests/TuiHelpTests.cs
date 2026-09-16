using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A footer that is a projection of the bindings rather than a string beside them
/// (<c>TUI-0022</c>).
/// </summary>
public sealed class TuiHelpTests
{
    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    [Fact]
    public void A_key_that_does_not_apply_is_not_described()
    {
        var editing = false;
        var keys = new TuiShortcuts();

        keys.On("q", "q", "quit", () => { });
        keys.When(() => editing, () => keys.On("escape", "Esc", "stop", () => { }));
        keys.When(() => !editing, () => keys.On("e", "e", "edit", () => { }));

        var help = new TuiHelp { Table = keys };

        Assert.Equal(["q", "e"], help.Describes().Select(key => key.Label));

        editing = true;

        Assert.Equal(["q", "Esc"], help.Describes().Select(key => key.Label));
    }

    [Fact]
    public void A_key_that_does_not_apply_does_not_fire()
    {
        // The other half, and the more important one. Describing a key and then answering
        // it anyway would be a footer that tells the truth about a screen that does not.
        var editing = false;
        var fired = 0;
        var keys = new TuiShortcuts();

        keys.When(() => editing, () => keys.On("e", "e", "edit", () => fired += 1));

        Assert.False(keys.TryHandle(new ConsoleKeyInfo('e', default, false, false, false), out _));
        Assert.Equal(0, fired);

        editing = true;

        Assert.True(keys.TryHandle(new ConsoleKeyInfo('e', default, false, false, false), out _));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Conditions_nest_rather_than_replace_each_other()
    {
        var outer = true;
        var inner = true;
        var keys = new TuiShortcuts();

        keys.When(() => outer, () => keys.When(() => inner, () => keys.On("x", "x", "go", () => { })));

        Assert.Single(keys.Available);

        inner = false;

        Assert.Empty(keys.Available);

        inner = true;
        outer = false;

        Assert.Empty(keys.Available);
    }

    [Fact]
    public void A_condition_that_throws_is_taken_as_no()
    {
        // A footer is not worth ending a screen over, and a binding whose condition cannot
        // be evaluated is one nobody should be told about either.
        var keys = new TuiShortcuts();

        keys.When(() => throw new InvalidOperationException("nope"), () => keys.On("x", "x", "go", () => { }));

        Assert.Empty(keys.Available);
        Assert.False(keys.TryHandle(new ConsoleKeyInfo('x', default, false, false, false), out _));
    }

    [Fact]
    public void One_line_by_default_and_a_list_when_asked()
    {
        var keys = new TuiShortcuts();

        keys.On("q", "q", "quit", () => { });
        keys.On("e", "e", "edit", () => { });

        var help = new TuiHelp { Table = keys, Separator = "  " };

        Assert.Equal(["q quit  e edit"], Render(help, 40, 1));

        help.Full = true;

        Assert.Equal(["q  quit", "e  edit"], Render(help, 40, 2));
    }

    [Fact]
    public void Whether_to_show_everything_can_be_asked_rather_than_set()
    {
        var everything = false;
        var keys = new TuiShortcuts();

        keys.On("q", "q", "quit", () => { });

        var help = new TuiHelp { Table = keys, FullWhen = () => everything };

        Assert.Equal(1, help.Measure(TuiConstraints.Unbounded).Height);

        everything = true;

        Assert.Equal(1, help.Measure(TuiConstraints.Unbounded).Height);
        Assert.Equal(["q  quit"], Render(help, 20, 1));
    }

    [Fact]
    public void The_screen_shows_the_keys_that_would_answer_from_where_the_keyboard_is()
    {
        // A footer that listed every table would describe a screen nobody is using. What
        // answers depends on where the keyboard is, so that is what it shows.
        var inner = new TuiTextField("draft") { Id = "field" };
        var help = new TuiHelp();

        inner.Keys = new TuiShortcuts();
        inner.Keys.On("ctrl+r", "^R", "rename", () => { });

        var root = new TuiStack(TuiOrientation.Vertical) { Items = [inner, help] };

        root.Keys = new TuiShortcuts();
        root.Keys.On("q", "q", "quit", () => { });

        using var screen = new TuiDeclarativeScreen(root, []);
        screen.Render(new TuiSize(40, 3));

        // Nearest the keyboard first: the field's own key, then the screen's.
        Assert.Equal(["^R", "q"], help.Describes().Select(key => key.Label));
    }

    [Fact]
    public void A_chord_bound_twice_is_shown_once_by_whichever_would_answer()
    {
        var inner = new TuiTextField("draft");
        var help = new TuiHelp();
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [inner, help] };

        inner.Keys = new TuiShortcuts();
        inner.Keys.On("escape", "Esc", "close the dialog", () => { });

        root.Keys = new TuiShortcuts();
        root.Keys.On("escape", "Esc", "leave the screen", () => { });

        using var screen = new TuiDeclarativeScreen(root, []);
        screen.Render(new TuiSize(40, 3));

        var described = Assert.Single(help.Describes());

        Assert.Equal("close the dialog", described.Description);
    }

    [Fact]
    public void A_key_written_in_markup_carries_its_condition()
    {
        var editing = false;

        var widget = TuiTreeBuilder.Build(
            new Dictionary<string, object?>
            {
                ["Column"] = new object?[] { new Dictionary<string, object?> { ["Help"] = "" } },
                ["Keys"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Key"] = "e",
                        ["Does"] = "edit",
                        ["Do"] = new Asked(() => null),
                        ["When"] = new Asked(() => !editing),
                    },
                },
            },
            registry: null,
            invoke: (callable, _) => ((Asked)callable).Fire(),
            out _);

        var keys = Assert.IsType<TuiShortcuts>(widget.Keys);

        Assert.Single(keys.Available);

        editing = true;

        Assert.Empty(keys.Available);
    }

    private sealed class Asked(Func<object?> answer) : Tosh.Runtime.IShellCallable
    {
        public object? Fire() => answer();

        public string CallableName => "asked";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 0;

        public IAsyncEnumerable<object?> InvokeAsync(Tosh.Runtime.CommandContext context)
            => throw new NotSupportedException();
    }
}

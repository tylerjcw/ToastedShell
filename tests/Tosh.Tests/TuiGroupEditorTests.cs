using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A list of named values, each one toggled or edited in place — <c>TUI-0002</c>.
/// </summary>
/// <remarks>
/// <see cref="TuiGroupEditorState{TItem}"/> keeps the cursor and its key and reads the keys,
/// and does not draw. Unlike <see cref="TuiCollectionEditor"/> this widget does not act on
/// the rows: a collection is a list of values, where "add this one" means the same thing
/// everywhere, while a group is a list of settings and what toggling one means belongs to
/// whoever defined it.
/// </remarks>
public sealed class TuiGroupEditorTests
{
    private const int Width = 32;
    private const int Height = 5;

    private sealed record Setting(string Name, string Current);

    private static readonly object?[] Settings =
    [
        new Setting("Verbose", "off"),
        new Setting("Colour", "auto"),
    ];

    private static TuiGroupEditor Laid(TuiGroupEditor group)
    {
        group.Measure(new TuiConstraints(Width, Height));
        group.Arrange(new TuiRect(0, 0, Width, Height));

        return group;
    }

    private static TuiGroupEditor Group() => Laid(new TuiGroupEditor(Settings)
    {
        LabelProperty = "Name",
        ValueProperty = "Current",
    });

    private static string Render(TuiGroupEditor group)
    {
        var buffer = new TuiBuffer(new TuiSize(Width, Height));

        Laid(group).Draw(new TuiSurface(buffer, new TuiRect(0, 0, Width, Height)));

        return string.Join('\n', Enumerable.Range(0, Height).Select(row => buffer.RowText(row).TrimEnd()));
    }

    private static bool Press(TuiGroupEditor group, ConsoleKey key)
        => group.OnInput(TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false)));

    [Fact]
    public void It_draws_each_row_as_name_and_value()
    {
        var text = Render(Group());

        Assert.Contains("> Verbose: off", text, StringComparison.Ordinal);
        Assert.Contains("  Colour: auto", text, StringComparison.Ordinal);
    }

    /// <summary>A row with no value is its name alone, not a dangling colon.</summary>
    [Fact]
    public void A_row_without_a_value_is_just_its_name()
    {
        var text = Render(Laid(new TuiGroupEditor([new Setting("Verbose", string.Empty)])
        {
            LabelProperty = "Name",
            ValueProperty = "Current",
        }));

        Assert.Contains("> Verbose", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Verbose:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_arrows_move_the_cursor()
    {
        var group = Group();

        Assert.True(Press(group, ConsoleKey.DownArrow));
        Assert.Contains("> Colour: auto", Render(group), StringComparison.Ordinal);
    }

    /// <summary>
    /// The widget reports which row was asked about and leaves the answer to the caller —
    /// it does not decide what toggling a setting means.
    /// </summary>
    [Fact]
    public void Space_asks_for_the_row_under_the_cursor_to_be_toggled()
    {
        object? toggled = null;
        var group = Group();
        group.Toggled = row => toggled = row;

        Assert.True(Press(group, ConsoleKey.Spacebar));
        Assert.Equal(Settings[0], toggled);
    }

    [Theory]
    [InlineData(ConsoleKey.Enter)]
    [InlineData(ConsoleKey.E)]
    public void Enter_or_e_asks_to_edit_it(ConsoleKey key)
    {
        object? edited = null;
        var group = Group();
        group.Edited = row => edited = row;

        Assert.True(Press(group, key));
        Assert.Equal(Settings[0], edited);
    }

    [Fact]
    public void T_asks_to_edit_it_as_raw_text()
    {
        object? raw = null;
        var group = Group();
        group.RawEdited = row => raw = row;

        Assert.True(Press(group, ConsoleKey.T));
        Assert.Equal(Settings[0], raw);
    }

    /// <summary>The row asked about follows the cursor, not the order it was declared in.</summary>
    [Fact]
    public void The_row_asked_about_is_the_one_under_the_cursor()
    {
        object? edited = null;
        var group = Group();
        group.Edited = row => edited = row;

        Press(group, ConsoleKey.DownArrow);
        Press(group, ConsoleKey.Enter);

        Assert.Equal(Settings[1], edited);
    }

    [Fact]
    public void Escape_closes_the_group()
    {
        var closed = false;
        var group = Group();
        group.Closed = () => closed = true;

        Assert.True(Press(group, ConsoleKey.Escape));
        Assert.True(closed);
    }

    /// <summary>An empty group answers its keys rather than passing them to what is behind.</summary>
    [Fact]
    public void An_empty_group_still_answers_its_keys()
        => Assert.True(Press(Laid(new TuiGroupEditor()), ConsoleKey.Spacebar));

    /// <summary>A key with nothing to do here is left for whatever else wants it.</summary>
    [Fact]
    public void An_unrelated_key_is_not_swallowed()
        => Assert.False(Press(Group(), ConsoleKey.F5));
}

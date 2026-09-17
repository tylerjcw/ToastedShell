using Tosh.Tui;

namespace Tosh.Tests;

/// <summary>
/// A screen's own keys, in a table rather than in the middle of a method.
/// </summary>
/// <remarks>
/// The table is not what fixes <c>TOSH-0011</c> — asking it after the focused widget is.
/// What the table removes is the possibility of asking it first by accident, which is what
/// a line of code in a method invites.
/// </remarks>
public sealed class TuiShortcutsTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, ConsoleModifiers modifiers = 0)
        => new('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control));

    private static ConsoleKeyInfo Type(char character)
        => new(character, ConsoleKey.None, false, false, false);

    [Fact]
    public void A_registered_key_runs_its_action()
    {
        var quit = 0;
        var shortcuts = new TuiShortcuts().On(ConsoleKey.Q, "q", "quit", () => quit += 1);

        Assert.True(shortcuts.TryHandle(Key(ConsoleKey.Q), out var result));
        Assert.Equal(TuiScreenResult.Continue, result);
        Assert.Equal(1, quit);
    }

    [Fact]
    public void An_unregistered_key_is_left_alone()
    {
        var shortcuts = new TuiShortcuts().Exit(ConsoleKey.Q, "q", "quit");

        Assert.False(shortcuts.TryHandle(Key(ConsoleKey.W), out _));
    }

    [Fact]
    public void A_key_that_ends_the_screen_says_so()
    {
        var shortcuts = new TuiShortcuts().Exit(ConsoleKey.Escape, "Esc", "close");

        Assert.True(shortcuts.TryHandle(Key(ConsoleKey.Escape), out var result));
        Assert.Equal(TuiScreenResult.Exit, result);
    }

    [Fact]
    public void A_character_is_matched_as_the_character_that_was_typed()
    {
        // `/` is not the same ConsoleKey on every keyboard, and a shortcut written as `/`
        // means the character the reader typed.
        var opened = false;
        var shortcuts = new TuiShortcuts().On('/', "/", "search", () => opened = true);

        Assert.True(shortcuts.TryHandle(Type('/'), out _));
        Assert.True(opened);
    }

    [Fact]
    public void A_modifier_the_shortcut_did_not_ask_for_is_a_different_chord()
    {
        var quit = 0;
        var shortcuts = new TuiShortcuts().On(ConsoleKey.Q, "q", "quit", () => quit += 1);

        Assert.False(shortcuts.TryHandle(Key(ConsoleKey.Q, ConsoleModifiers.Control), out _));
        Assert.False(shortcuts.TryHandle(Key(ConsoleKey.Q, ConsoleModifiers.Alt), out _));

        // Shift is how a capital arrives at all, so it is not a different chord.
        Assert.True(shortcuts.TryHandle(Key(ConsoleKey.Q, ConsoleModifiers.Shift), out _));
        Assert.Equal(1, quit);
    }

    [Fact]
    public void The_first_registration_for_a_key_is_the_one_that_answers()
    {
        var which = 0;
        var shortcuts = new TuiShortcuts()
            .On(ConsoleKey.Q, "q", "first", () => which = 1)
            .On(ConsoleKey.Q, "q", "second", () => which = 2);

        shortcuts.TryHandle(Key(ConsoleKey.Q), out _);

        Assert.Equal(1, which);
    }

    [Fact]
    public void The_table_can_describe_itself_so_a_footer_cannot_drift()
    {
        var shortcuts = new TuiShortcuts()
            .Exit(ConsoleKey.Q, "q", "quit")
            .On('/', "/", "search", static () => { })
            .On(ConsoleKey.Escape, "Esc", string.Empty, static () => { });

        // The one with nothing to say stays out of the line rather than appearing blank.
        Assert.Equal("q quit  / search", shortcuts.Describe());
    }

    [Theory]
    [InlineData("space", ConsoleKey.Spacebar)]
    [InlineData("esc", ConsoleKey.Escape)]
    [InlineData("pgdn", ConsoleKey.PageDown)]
    [InlineData("up", ConsoleKey.UpArrow)]
    [InlineData("left", ConsoleKey.LeftArrow)]
    public void A_key_can_be_spelled_the_way_a_reader_would_write_it(string chord, ConsoleKey expected)
    {
        // `" "` cannot be spelled at all: a chord is trimmed before it is read, so a
        // literal space is lost, which left the one key every "pause" binding wants
        // unregisterable. The enum's own names are not what anybody writes either.
        var shortcuts = new TuiShortcuts();
        var fired = 0;

        shortcuts.On(chord, chord, "go", () => fired += 1);

        Assert.True(shortcuts.TryHandle(new ConsoleKeyInfo('\0', expected, false, false, false), out _));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void The_enum_names_still_work_for_anyone_who_prefers_them()
    {
        var shortcuts = new TuiShortcuts();

        shortcuts.On("spacebar", "space", "go", () => { });
        shortcuts.On("f5", "F5", "refresh", () => { });

        Assert.True(shortcuts.TryHandle(new ConsoleKeyInfo('\0', ConsoleKey.Spacebar, false, false, false), out _));
        Assert.True(shortcuts.TryHandle(new ConsoleKeyInfo('\0', ConsoleKey.F5, false, false, false), out _));
    }

    /// <summary>
    /// A binding can read the press, for the one thing a chord cannot say (<c>TUI-0022</c>).
    /// </summary>
    /// <remarks>
    /// Shift is ignored when matching, on purpose: it is how a capital arrives, so a table
    /// that treated it as a modifier could not register <c>r</c> and <c>R</c> as two
    /// bindings. The cost is that <c>Tab</c> and <c>Shift+Tab</c> are one binding, and one
    /// line in the footer — so the action is what has to tell them apart.
    /// </remarks>
    [Fact]
    public void A_binding_can_be_told_how_it_was_pressed()
    {
        var shortcuts = new TuiShortcuts();
        var reversed = new List<bool>();

        shortcuts.On(ConsoleKey.Tab, "Tab", "switch panes", key =>
        {
            reversed.Add(key.Modifiers.HasFlag(ConsoleModifiers.Shift));
            return TuiScreenResult.Continue;
        });

        Assert.True(shortcuts.TryHandle(Tab(shift: false), out _));
        Assert.True(shortcuts.TryHandle(Tab(shift: true), out _));

        Assert.Equal([false, true], reversed);

        // And it is one line in the footer, not two.
        Assert.Equal("Tab switch panes", shortcuts.Describe());
    }

    /// <summary>
    /// A note describes a key without answering it (<c>TUI-0022</c>).
    /// </summary>
    /// <remarks>
    /// For a key a widget owns. The screen has to be able to describe an editor's
    /// <c>Enter</c> without registering a second dispatcher for it.
    /// </remarks>
    [Fact]
    public void A_note_is_described_but_never_fires()
    {
        var shortcuts = new TuiShortcuts();

        shortcuts.Note("Enter", "stage");

        Assert.Equal("Enter stage", shortcuts.Describe());

        // Nothing answers it — including the key it happens to be labelled with.
        Assert.False(shortcuts.TryHandle(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), out _));
    }

    /// <summary>A note is conditional the same way a binding is.</summary>
    /// <remarks>
    /// This is the whole point of it: the condition under which a key is described is the
    /// same expression the widget's dispatcher switches on, so the footer cannot go on
    /// offering a key after the thing that answered it has closed.
    /// </remarks>
    [Fact]
    public void A_note_appears_only_while_its_condition_holds()
    {
        var shortcuts = new TuiShortcuts();
        var editing = false;

        shortcuts.When(() => editing, () => shortcuts.Note("Esc", "cancel"));

        Assert.Equal(string.Empty, shortcuts.Describe());

        editing = true;

        Assert.Equal("Esc cancel", shortcuts.Describe());
    }

    private static ConsoleKeyInfo Tab(bool shift)
        => new('\t', ConsoleKey.Tab, shift, false, false);
}

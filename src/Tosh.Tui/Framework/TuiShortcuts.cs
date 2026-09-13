namespace Tosh.Tui;

/// <summary>One registered key, and what it does.</summary>
/// <param name="Description">
/// What a footer would say about it. Kept beside the action so the help line and the
/// behaviour cannot drift apart.
/// </param>
public sealed record TuiShortcut(
    ConsoleKey Key,
    char Character,
    ConsoleModifiers Modifiers,
    string Label,
    string Description,
    Func<TuiScreenResult> Action);

/// <summary>
/// A screen's own keys, held in a table rather than tested at the top of a handler.
/// </summary>
/// <remarks>
/// <para>
/// Both browsers checked their quit key before they checked where focus was, so typing
/// <c>q</c> into either search box quit the browser (<c>TOSH-0011</c>). That is not a slip
/// anyone should be expected to avoid twice — it is what happens when a shortcut is a line
/// of code in the middle of a method, because then whether it fires depends on where the
/// line sits rather than on what it means.
/// </para>
/// <para>
/// A table cannot be in the wrong place. The caller asks it once, after the focused widget
/// and its ancestors have declined the key, and the ordering is then a property of the
/// screen rather than of the order somebody happened to write two <c>if</c>s
/// (<c>TUI-0006</c>).
/// </para>
/// </remarks>
public sealed class TuiShortcuts
{
    private readonly List<TuiShortcut> _shortcuts = [];

    /// <summary>Every registered key, in the order they were added.</summary>
    public IReadOnlyList<TuiShortcut> Registered => _shortcuts;

    /// <summary>Registers a key that ends the screen.</summary>
    public TuiShortcuts Exit(ConsoleKey key, string label, string description)
        => On(key, label, description, static () => TuiScreenResult.Exit);

    /// <summary>Registers a key.</summary>
    public TuiShortcuts On(ConsoleKey key, string label, string description, Func<TuiScreenResult> action)
        => Add(key, '\0', ConsoleModifiers.None, label, description, action);

    /// <summary>Registers a key that does something and leaves the screen up.</summary>
    public TuiShortcuts On(ConsoleKey key, string label, string description, Action action)
        => On(key, label, description, () =>
        {
            action();
            return TuiScreenResult.Continue;
        });

    /// <summary>Registers a printable character.</summary>
    /// <remarks>
    /// Matched on the character rather than the <see cref="ConsoleKey"/>, because that is
    /// what the reader typed: <c>ConsoleKey.Oem2</c> is not <c>/</c> on every keyboard.
    /// </remarks>
    public TuiShortcuts On(char character, string label, string description, Action action)
        => Add(default, character, ConsoleModifiers.None, label, description, () =>
        {
            action();
            return TuiScreenResult.Continue;
        });

    /// <summary>
    /// Registers a key written as a chord.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>"q"</c>, <c>"ctrl+s"</c>, <c>"shift+tab"</c>, <c>"f5"</c>, <c>"/"</c>. This is the
    /// spelling a script writes, and the reason it exists is that the alternative — naming
    /// a <see cref="ConsoleKey"/> — is wrong for punctuation on a real terminal: .NET
    /// reports <c>Divide</c> for <c>/</c> and <c>None</c> for the brackets.
    /// </para>
    /// <para>
    /// A single printable character is matched as that character. Anything else is matched
    /// as a named key, so <c>"f5"</c> and <c>"enter"</c> mean what they say.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// One overload per name for the chord form, deliberately. A script assigning a
    /// function cannot choose between <c>Action</c> and <c>Func&lt;TuiScreenResult&gt;</c>
    /// — both are delegates its function converts to, so the first candidate wins and a
    /// handler that returns nothing fails at the keystroke rather than at the assignment.
    /// Ending the screen is <see cref="Exit(string, string, string)"/> instead of a return
    /// value, which reads better anyway.
    /// </remarks>
    public TuiShortcuts On(string chord, string label, string description, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var (key, character, modifiers) = ParseChord(chord);

        return Add(key, character, modifiers, label.Length > 0 ? label : chord, description, () =>
        {
            action();
            return TuiScreenResult.Continue;
        });
    }

    /// <summary>Registers a chord that ends the screen.</summary>
    public TuiShortcuts Exit(string chord, string label, string description)
    {
        var (key, character, modifiers) = ParseChord(chord);

        return Add(
            key,
            character,
            modifiers,
            label.Length > 0 ? label : chord,
            description,
            static () => TuiScreenResult.Exit);
    }

    /// <summary>Registers a chord that does something and then ends the screen.</summary>
    /// <remarks>Save and quit is one keystroke in every editor anyone has used.</remarks>
    public TuiShortcuts Exit(string chord, string label, string description, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var (key, character, modifiers) = ParseChord(chord);

        return Add(key, character, modifiers, label.Length > 0 ? label : chord, description, () =>
        {
            action();
            return TuiScreenResult.Exit;
        });
    }

    /// <summary>
    /// Reads a chord into the pieces a key press is matched on.
    /// </summary>
    /// <remarks>
    /// Unrecognised names produce a chord nothing will ever match, rather than a
    /// diagnostic: a screen with one dud binding should still run, and the footer built
    /// from the table is where the mistake shows.
    /// </remarks>
    internal static (ConsoleKey Key, char Character, ConsoleModifiers Modifiers) ParseChord(string? chord)
    {
        var text = (chord ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return (default, '\0', ConsoleModifiers.None);
        }

        var modifiers = ConsoleModifiers.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A trailing `+` is the plus key rather than a missing part: "ctrl++" is Ctrl and
        // the character, and splitting on `+` has already thrown that away.
        var final = text.EndsWith('+') ? "+" : parts.Length > 0 ? parts[^1] : string.Empty;
        var named = parts.Length > 0 && !text.EndsWith('+') ? parts[..^1] : parts;

        foreach (var part in named)
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ConsoleModifiers.Control,
                "alt" or "option" => ConsoleModifiers.Alt,
                "shift" => ConsoleModifiers.Shift,
                _ => ConsoleModifiers.None,
            };
        }

        // With a modifier, a letter has to be matched as a *key*. A terminal sends Ctrl+O
        // as the byte 0x0F, so its `KeyChar` is a control code and never the letter — a
        // chord matched on the character would simply never fire.
        var chorded = (modifiers & ~ConsoleModifiers.Shift) != ConsoleModifiers.None;

        if (final.Length == 1 && chorded)
        {
            var only = char.ToUpperInvariant(final[0]);

            if (only is >= 'A' and <= 'Z')
            {
                return (ConsoleKey.A + (only - 'A'), '\0', modifiers);
            }

            if (only is >= '0' and <= '9')
            {
                return (ConsoleKey.D0 + (only - '0'), '\0', modifiers);
            }
        }

        // Without one, the character is what the reader pressed and the only thing worth
        // matching: .NET reports `Divide` for `/` and `None` for the brackets.
        if (final.Length == 1 && !char.IsControl(final[0]))
        {
            return (default, final[0], modifiers);
        }

        return Enum.TryParse<ConsoleKey>(final, ignoreCase: true, out var key)
            ? (key, '\0', modifiers)
            : (ConsoleKey.None, '\0', modifiers);
    }

    private TuiShortcuts Add(
        ConsoleKey key,
        char character,
        ConsoleModifiers modifiers,
        string label,
        string description,
        Func<TuiScreenResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        _shortcuts.Add(new TuiShortcut(key, character, modifiers, label, description, action));
        return this;
    }

    /// <summary>
    /// Runs the action for a key, if one is registered for it.
    /// </summary>
    /// <remarks>
    /// Ask this only once the focused widget has declined the key. A screen that asks
    /// first has the bug this class exists to remove.
    /// </remarks>
    public bool TryHandle(ConsoleKeyInfo key, out TuiScreenResult result)
    {
        foreach (var shortcut in _shortcuts)
        {
            if (!Matches(shortcut, key))
            {
                continue;
            }

            result = shortcut.Action();
            return true;
        }

        result = TuiScreenResult.Continue;
        return false;
    }

    /// <summary>The footer line these shortcuts describe.</summary>
    public string Describe(string separator = "  ")
        => string.Join(separator, _shortcuts
            .Where(shortcut => shortcut.Description.Length > 0)
            .Select(shortcut => $"{shortcut.Label} {shortcut.Description}"));

    private static bool Matches(TuiShortcut shortcut, ConsoleKeyInfo key)
    {
        // A modifier the shortcut did not ask for is a different chord: Ctrl+Q is not q.
        // Shift is left out of that rule, because it is how a capital arrives at all.
        var pressed = key.Modifiers & ~ConsoleModifiers.Shift;

        if (pressed != (shortcut.Modifiers & ~ConsoleModifiers.Shift))
        {
            return false;
        }

        return shortcut.Character != '\0'
            ? key.KeyChar == shortcut.Character
            : key.Key == shortcut.Key;
    }
}

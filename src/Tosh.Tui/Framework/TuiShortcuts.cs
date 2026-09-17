namespace Tosh.Tui;

/// <summary>
/// What a key can aim at, when it aims at part of the screen rather than at a function.
/// </summary>
/// <remarks>
/// <para>
/// A shortcut binds a chord to an action, and an action is a script function. That covers
/// every command that changes state — save, open, quit, refresh — and none that change
/// where the keyboard <em>is</em>, which is the other half of what keybindings are for.
/// <c>Ctrl+P</c> opens a palette and puts the caret in it. <c>/</c> focuses the search box.
/// </para>
/// <para>
/// The focus manager is private to the screen, rightly — a script should not be holding
/// one — so a key says what it wants by id, the same way everything else on a screen is
/// addressed, and whatever owns the tree does it (<c>TUI-0026</c>).
/// </para>
/// </remarks>
public interface ITuiAim
{
    /// <summary>Moves the keyboard to the widget with this id.</summary>
    /// <returns>Whether there was such a widget and it could take the keyboard.</returns>
    bool Focus(string id);

    /// <summary>Does whatever pressing Enter on that widget would do.</summary>
    /// <remarks>The keyboard does not move: <c>F5</c> should not take the caret out of
    /// whatever the reader was typing in.</remarks>
    bool Press(string id);

    /// <summary>Puts the keyboard back where it was before the last <see cref="Focus"/>.</summary>
    /// <remarks>What closing a palette, a menu or a search box wants.</remarks>
    bool Restore();
}

/// <summary>One registered key, and what it does.</summary>
/// <param name="Description">
/// What a footer would say about it. Kept beside the action so the help line and the
/// behaviour cannot drift apart.
/// </param>
/// <param name="Applies">
/// Whether this key means anything right now, or null when it always does. Half of what a
/// binding is: <c>i insert</c> applies outside the search box and nowhere else, and
/// <c>1</c>-<c>9</c> apply only when the current topic has related topics (<c>TUI-0022</c>).
/// </param>
/// <param name="Pressed">
/// Run in place of <paramref name="Action"/> when the key press itself decides what the
/// binding does. Shift is the reason this exists: it is how a capital arrives, so it
/// cannot distinguish one chord from another, which leaves <c>Shift+Tab</c> and
/// <c>Tab</c> as one binding that has to read the press to know which way to go.
/// </param>
public sealed record TuiShortcut(
    ConsoleKey Key,
    char Character,
    ConsoleModifiers Modifiers,
    string Label,
    string Description,
    Func<TuiScreenResult> Action,
    Func<bool>? Applies = null,
    Func<ConsoleKeyInfo, TuiScreenResult>? Pressed = null)
{
    /// <summary>Runs this binding for the key that matched it.</summary>
    internal TuiScreenResult Invoke(ConsoleKeyInfo key)
        => Pressed is { } pressed ? pressed(key) : Action();

    /// <summary>
    /// Whether this entry only describes a key, leaving something else to answer it.
    /// </summary>
    /// <remarks>
    /// For a key owned by a widget rather than by the screen — an editor's <c>Enter</c>,
    /// a dialog's arrows. The widget answering its own keys is the right design, and a
    /// screen that re-implemented them in its table to be able to describe them would have
    /// two dispatchers for one key, which is worse than the footer it was trying to fix.
    /// <para>
    /// A note never fires. What it buys is that the condition under which the key is
    /// described is the same expression the dispatcher switches on, so the two cannot
    /// disagree about <em>when</em> — which is the drift that made a footer offer
    /// <c>Esc cancel</c> on a screen with nothing to cancel.
    /// </para>
    /// </remarks>
    public bool IsNote { get; init; }

    /// <summary>Whether this key would answer if it were pressed now.</summary>
    /// <remarks>
    /// A predicate that throws is treated as "no". A footer is not worth ending a screen
    /// over, and a binding whose condition cannot be evaluated is one nobody should be
    /// told about either.
    /// </remarks>
    public bool IsAvailable
    {
        get
        {
            if (Applies is not { } applies)
            {
                return true;
            }

            try
            {
                return applies();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                                                  and not StackOverflowException)
            {
                return false;
            }
        }
    }
}

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

    /// <summary>
    /// What a key aiming at a widget goes through. Set by whatever owns the tree.
    /// </summary>
    /// <remarks>
    /// Null in a table nobody has hung on a screen — a test building one, say — and a key
    /// that aims at something then does nothing rather than throwing. A binding that does
    /// not currently apply should be inert, not fatal.
    /// </remarks>
    public ITuiAim? Aim { get; set; }

    /// <summary>Registers a key that ends the screen.</summary>
    public TuiShortcuts Exit(ConsoleKey key, string label, string description)
        => On(key, label, description, static () => TuiScreenResult.Exit);

    /// <summary>Registers a key.</summary>
    public TuiShortcuts On(ConsoleKey key, string label, string description, Func<TuiScreenResult> action)
        => Add(key, '\0', ConsoleModifiers.None, label, description, action);

    /// <summary>Registers a key whose action depends on how it was pressed.</summary>
    /// <remarks>
    /// For the one case the chord cannot express. <c>Matches</c> ignores Shift on purpose —
    /// it is how a capital arrives, so a table that treated it as a modifier could not
    /// register <c>r</c> and <c>R</c> as different things — and the cost is that
    /// <c>Shift+Tab</c> is the same binding as <c>Tab</c>. One entry, one description in
    /// the footer, and the action reads the press to know which way to cycle.
    /// </remarks>
    public TuiShortcuts On(
        ConsoleKey key,
        string label,
        string description,
        Func<ConsoleKeyInfo, TuiScreenResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        _shortcuts.Add(new TuiShortcut(
            key,
            '\0',
            ConsoleModifiers.None,
            label,
            description,
            static () => TuiScreenResult.Continue,
            _applies,
            action));

        return this;
    }

    /// <summary>
    /// Records a key that something else answers, so the footer can describe it.
    /// </summary>
    /// <remarks>
    /// See <see cref="TuiShortcut.IsNote"/>. Written under the same <see cref="When"/>
    /// condition the dispatcher uses, so the description appears exactly when the key works.
    /// </remarks>
    public TuiShortcuts Note(string label, string description)
    {
        _shortcuts.Add(new TuiShortcut(
            default,
            '\0',
            ConsoleModifiers.None,
            label,
            description,
            static () => TuiScreenResult.Continue,
            _applies)
        {
            IsNote = true,
        });

        return this;
    }

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

    /// <summary>Registers a chord that moves the keyboard to a widget, by id.</summary>
    /// <remarks><c>$keys.Focus("/", "/", "search", "query")</c> — the last argument is the
    /// widget's <c>Id</c>, not its caption.</remarks>
    public TuiShortcuts Focus(string chord, string label, string description, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var (key, character, modifiers) = ParseChord(chord);

        return Add(key, character, modifiers, label.Length > 0 ? label : chord, description, () =>
        {
            Aim?.Focus(id);
            return TuiScreenResult.Continue;
        });
    }

    /// <summary>Registers a chord that presses a widget, by id, without moving the keyboard.</summary>
    public TuiShortcuts Press(string chord, string label, string description, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var (key, character, modifiers) = ParseChord(chord);

        return Add(key, character, modifiers, label.Length > 0 ? label : chord, description, () =>
        {
            Aim?.Press(id);
            return TuiScreenResult.Continue;
        });
    }

    /// <summary>Registers a chord that puts the keyboard back where it was.</summary>
    public TuiShortcuts Back(string chord, string label, string description)
    {
        var (key, character, modifiers) = ParseChord(chord);

        return Add(key, character, modifiers, label.Length > 0 ? label : chord, description, () =>
        {
            Aim?.Restore();
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

        // A few names that are what a reader would write rather than what the enum calls
        // them. `" "` cannot be spelled at all — the chord is trimmed before it is read, so
        // a literal space is lost — which left the one key every "pause" binding wants
        // unregisterable.
        var alias = final.ToLowerInvariant() switch
        {
            "space" or "spacebar" => ConsoleKey.Spacebar,
            "esc" => ConsoleKey.Escape,
            "del" => ConsoleKey.Delete,
            "ins" => ConsoleKey.Insert,
            "pgup" => ConsoleKey.PageUp,
            "pgdn" or "pgdown" => ConsoleKey.PageDown,
            "up" => ConsoleKey.UpArrow,
            "down" => ConsoleKey.DownArrow,
            "left" => ConsoleKey.LeftArrow,
            "right" => ConsoleKey.RightArrow,
            _ => ConsoleKey.None,
        };

        if (alias != ConsoleKey.None)
        {
            return (alias, '\0', modifiers);
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

        _shortcuts.Add(new TuiShortcut(key, character, modifiers, label, description, action, _applies));
        return this;
    }

    /// <summary>The condition the next registrations are made under.</summary>
    private Func<bool>? _applies;

    /// <summary>
    /// Registers keys that apply only sometimes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A condition wraps a group rather than being an argument to each one, because keys
    /// that apply together are written together and repeating the predicate six times is
    /// how the seventh comes to disagree:
    /// </para>
    /// <code>
    /// $keys.When(func() => not $searching, func() {
    ///     $keys.On("i", "i", "insert", &amp;Insert) | ignore
    ///     $keys.On("e", "e", "edit", &amp;Edit) | ignore
    /// })
    /// </code>
    /// <para>
    /// A key that does not apply does not fire and is not described, so the footer cannot
    /// claim a key that would do nothing — which is the whole point: the help is a
    /// projection of what would answer, not a sentence maintained beside it.
    /// </para>
    /// </remarks>
    public TuiShortcuts When(Func<bool> applies, Action register)
    {
        ArgumentNullException.ThrowIfNull(applies);
        ArgumentNullException.ThrowIfNull(register);

        var outer = _applies;

        // Nested conditions are both conditions, so a group inside a group applies only
        // where each of them does.
        _applies = outer is null ? applies : () => outer() && applies();

        try
        {
            register();
        }
        finally
        {
            _applies = outer;
        }

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
            // The condition is checked as well as the chord, so a key that does not apply
            // does not fire. Describing it and then answering it anyway would be a footer
            // that tells the truth about a screen that does not.
            if (shortcut.IsNote || !Matches(shortcut, key) || !shortcut.IsAvailable)
            {
                continue;
            }

            result = shortcut.Invoke(key);
            return true;
        }

        result = TuiScreenResult.Continue;
        return false;
    }

    /// <summary>The keys that would answer right now, in the order they were added.</summary>
    public IEnumerable<TuiShortcut> Available => _shortcuts.Where(shortcut => shortcut.IsAvailable);

    /// <summary>The footer line these shortcuts describe.</summary>
    /// <remarks>
    /// Only the ones that apply. A footer offering a key that would do nothing is the
    /// drift this table exists to remove, and a condition is the other half of it.
    /// </remarks>
    public string Describe(string separator = "  ")
        => string.Join(separator, Available
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

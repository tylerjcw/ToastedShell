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

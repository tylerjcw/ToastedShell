namespace Tosh.Runtime;

/// <summary>Where a terminal is being displayed, which decides most of what it can do.</summary>
public enum TerminalSurface
{
    /// <summary>A window under a graphical environment.</summary>
    Window,

    /// <summary>A kernel console — a bare TTY or a framebuffer, with no graphical environment.</summary>
    Console,

    /// <summary>A terminal multiplexer, which relays only what it understands.</summary>
    Multiplexer,

    /// <summary>A serial line, a dumb terminal, or something else that answers for very little.</summary>
    Basic,
}

/// <summary>
/// Which terminal TōSh is talking to, and what it can be asked to do.
/// </summary>
/// <remarks>
/// <para>
/// Capabilities used to be decided where they were used: the graphics protocol was worked out
/// from the environment, while the keyboard, focus and paste protocols were switched on for
/// everything and given an environment variable to switch off again. So there was no one place
/// that said what the terminal was, nothing a script could read, and no way to correct a guess.
/// </para>
/// <para>
/// Detection is by environment rather than by asking the terminal. A query — writing
/// <c>CSI ? u</c> and reading the reply — is the more certain answer, but it has to happen
/// before the first prompt and costs a round trip against a terminal that may never reply.
/// </para>
/// </remarks>
public sealed record TerminalProfile(
    string Name,
    string Program,
    string Term,
    TerminalSurface Surface,
    bool Remote,
    bool TrueColor,
    int Colors,
    bool Unicode,
    bool KittyKeyboard,
    bool BracketedPaste,
    bool FocusReporting,
    string Graphics)
{
    /// <summary>Whether a multiplexer sits between TōSh and the terminal.</summary>
    public bool Multiplexed => Surface is TerminalSurface.Multiplexer;

    /// <summary>The terminal's width in columns, or 80 when there is no terminal to ask.</summary>
    public int Width => Measure(static () => System.Console.WindowWidth, 80);

    /// <summary>The terminal's height in rows, or 24 when there is no terminal to ask.</summary>
    public int Height => Measure(static () => System.Console.WindowHeight, 24);

    /// <summary>
    /// Asked each time rather than recorded, because a window is resized while TōSh runs.
    /// Reading the size throws when output is redirected, and answers zero in some consoles;
    /// either way a caller wants a number it can lay text out in.
    /// </summary>
    private static int Measure(Func<int> read, int fallback)
    {
        try
        {
            var value = read();
            return value > 0 ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Whether this is a kernel console rather than a window.</summary>
    public bool Console => Surface is TerminalSurface.Console;

    /// <summary>What the environment said about the terminal when this process started.</summary>
    /// <remarks>
    /// Detected once: the environment that names a terminal does not change under a running
    /// process, and every caller that asks should get the same answer.
    ///
    /// Lazily, because static initialisers run in declaration order and this one reads the
    /// tables below it. Computed eagerly here, they are still null when it runs and the type
    /// fails to initialise at all.
    /// </remarks>
    public static TerminalProfile Detected => LazyDetected.Value;

    private static readonly Lazy<TerminalProfile> LazyDetected =
        new(() => Detect(Environment.GetEnvironmentVariable));

    /// <summary>What is assumed when nothing identifies the terminal.</summary>
    public static TerminalProfile Unknown { get; } = new(
        "unknown", string.Empty, string.Empty,
        TerminalSurface.Window, Remote: false,
        TrueColor: false, Colors: 256, Unicode: true,
        KittyKeyboard: true, BracketedPaste: true, FocusReporting: true,
        Graphics: "half-blocks");

    // ── the tables ──
    //
    // A terminal that sets a variable of its own is saying what it is, and that beats `TERM`,
    // which it usually sets to `xterm-256color` for the sake of software that expects it.

    private static readonly (string Variable, string Name)[] Markers =
    [
        ("GHOSTTY_RESOURCES_DIR", "ghostty"),
        ("GHOSTTY_BIN_DIR", "ghostty"),
        ("KITTY_WINDOW_ID", "kitty"),
        ("WEZTERM_PANE", "wezterm"),
        ("WEZTERM_EXECUTABLE", "wezterm"),
        ("ALACRITTY_WINDOW_ID", "alacritty"),
        ("ALACRITTY_SOCKET", "alacritty"),
        ("KONSOLE_VERSION", "konsole"),
        ("KONSOLE_DBUS_SESSION", "konsole"),
        ("ITERM_SESSION_ID", "iterm2"),
        ("TERMINATOR_UUID", "terminator"),
        ("TILIX_ID", "tilix"),
        ("WT_SESSION", "windows-terminal"),
        ("MLTERM", "mlterm"),
        ("XTERM_VERSION", "xterm"),
    ];

    /// <summary>Terminals named by <c>TERM_PROGRAM</c> or by a <c>TERM</c> that mentions them.</summary>
    private static readonly (string Fragment, string Name)[] Names =
    [
        ("ghostty", "ghostty"),
        ("kitty", "kitty"),
        ("wezterm", "wezterm"),
        ("alacritty", "alacritty"),
        ("contour", "contour"),
        ("foot", "foot"),
        ("rio", "rio"),
        ("wayst", "wayst"),
        ("darktile", "darktile"),
        ("zutty", "zutty"),
        ("konsole", "konsole"),
        ("terminator", "terminator"),
        ("tilix", "tilix"),
        ("iterm", "iterm2"),
        ("Apple_Terminal", "apple-terminal"),
        ("vscode", "vscode"),
        ("hyper", "hyper"),
        ("warp", "warp"),
        ("tabby", "tabby"),
        ("mintty", "mintty"),
        ("ms-terminal", "windows-terminal"),
        ("putty", "putty"),
        ("cygwin", "cygwin"),
        ("rxvt", "urxvt"),
        ("mlterm", "mlterm"),
        ("st-", "st"),
        ("xterm", "xterm"),
    ];

    /// <summary>The console and near-console terminals, and the surface each one is.</summary>
    private static readonly (string Term, string Name, TerminalSurface Surface)[] Surfaces =
    [
        ("linux", "linux-console", TerminalSurface.Console),
        ("fbterm", "fbterm", TerminalSurface.Console),
        ("bterm", "bterm", TerminalSurface.Console),
        ("kmscon", "kmscon", TerminalSurface.Console),
        ("vt100", "vt100", TerminalSurface.Basic),
        ("vt220", "vt220", TerminalSurface.Basic),
        ("dumb", "dumb", TerminalSurface.Basic),
    ];

    /// <summary>Terminals that speak the Kitty graphics protocol.</summary>
    private static readonly string[] KittyGraphics = ["ghostty", "kitty", "wezterm", "konsole"];

    /// <summary>Terminals that speak sixel.</summary>
    private static readonly string[] SixelGraphics =
        ["foot", "contour", "iterm2", "mlterm", "rio", "windows-terminal", "xterm", "wayst"];

    /// <summary>Terminals known to answer 24-bit colour without declaring <c>COLORTERM</c>.</summary>
    private static readonly string[] TrueColorTerminals =
    [
        "ghostty", "kitty", "wezterm", "alacritty", "foot", "contour", "konsole", "iterm2",
        "vscode", "hyper", "warp", "tabby", "rio", "windows-terminal", "terminator", "tilix",
        "mintty", "wayst", "darktile", "vte",
    ];

    /// <summary>Reads the terminal's identity and capabilities out of the environment.</summary>
    public static TerminalProfile Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var program = environment("TERM_PROGRAM") ?? string.Empty;
        var term = environment("TERM") ?? string.Empty;
        var remote =
            !string.IsNullOrEmpty(environment("SSH_TTY")) ||
            !string.IsNullOrEmpty(environment("SSH_CONNECTION"));

        var (name, surface) = Identify(environment, program, term);

        return ForSurface(name, surface) with
        {
            Program = program,
            Term = term,
            Remote = remote,
            TrueColor = HasTrueColor(environment, name, surface),
            Colors = ColorsFor(environment, name, term, surface),
        };
    }

    /// <summary>The terminal's name and the surface it is displayed on.</summary>
    private static (string Name, TerminalSurface Surface) Identify(
        Func<string, string?> environment,
        string program,
        string term)
    {
        // A multiplexer first: it is what TōSh is actually writing to, whatever is behind it.
        if (!string.IsNullOrEmpty(environment("TMUX")) || Mentions(term, "tmux"))
        {
            return ("tmux", TerminalSurface.Multiplexer);
        }

        if (term.StartsWith("screen", StringComparison.OrdinalIgnoreCase))
        {
            return ("screen", TerminalSurface.Multiplexer);
        }

        // A kernel console names itself in `TERM` and sets none of the markers below, because
        // there is no graphical environment for one to have come from.
        foreach (var (consoleTerm, consoleName, surface) in Surfaces)
        {
            if (term.Equals(consoleTerm, StringComparison.OrdinalIgnoreCase) ||
                term.StartsWith(consoleTerm + "-", StringComparison.OrdinalIgnoreCase))
            {
                return (consoleName, surface);
            }
        }

        foreach (var (variable, marker) in Markers)
        {
            if (!string.IsNullOrEmpty(environment(variable))) { return (marker, TerminalSurface.Window); }
        }

        foreach (var (fragment, named) in Names)
        {
            if (Mentions(program, fragment)) { return (named, TerminalSurface.Window); }
        }

        // VTE names the toolkit rather than the terminal, and it comes before `TERM` because
        // a VTE terminal almost always sets `TERM=xterm-256color` for the sake of software
        // that expects it. Read the other way round, every GNOME-family terminal is an xterm
        // — and inherits xterm's sixel, which VTE does not have.
        if (!string.IsNullOrEmpty(environment("VTE_VERSION"))) { return ("vte", TerminalSurface.Window); }

        foreach (var (fragment, named) in Names)
        {
            if (Mentions(term, fragment)) { return (named, TerminalSurface.Window); }
        }

        // Nothing named it. Without a graphical environment it is a console of some kind,
        // whatever `TERM` claims, and should not be asked for what a window can do.
        var graphical =
            !string.IsNullOrEmpty(environment("DISPLAY")) ||
            !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"));

        return graphical || !string.IsNullOrEmpty(environment("SSH_TTY"))
            ? ("unknown", TerminalSurface.Window)
            : ("unknown", TerminalSurface.Basic);
    }

    /// <summary>The capabilities a named terminal on a given surface has.</summary>
    public static TerminalProfile ForSurface(string name, TerminalSurface surface)
    {
        var known = (name ?? string.Empty).Trim().ToLowerInvariant();

        // A console draws from a fixed font in a fixed palette, a multiplexer relays only what
        // it understands, and a dumb terminal answers for almost nothing. None of the three can
        // be asked for a protocol, and asking anyway sends the request itself to the screen.
        var rich = surface is TerminalSurface.Window;

        return new TerminalProfile(
            Name: known,
            Program: string.Empty,
            Term: string.Empty,
            Surface: surface,
            Remote: false,
            TrueColor: rich && TrueColorTerminals.Contains(known),
            Colors: surface switch
            {
                TerminalSurface.Console => 16,
                TerminalSurface.Basic => 8,
                _ => 256,
            },
            Unicode: surface is not (TerminalSurface.Console or TerminalSurface.Basic),
            KittyKeyboard: rich,
            BracketedPaste: surface is not TerminalSurface.Basic,
            FocusReporting: rich,
            Graphics: GraphicsFor(known, surface));
    }

    /// <summary>The picture protocol a terminal understands.</summary>
    private static string GraphicsFor(string name, TerminalSurface surface)
    {
        // A picture sent through a multiplexer lands on whichever pane it decided to draw,
        // because it does not know it was sent. A console cannot show one at all.
        if (surface is TerminalSurface.Multiplexer) { return "half-blocks"; }
        if (surface is TerminalSurface.Console or TerminalSurface.Basic) { return "none"; }

        if (KittyGraphics.Contains(name)) { return "kitty"; }

        // Kitty first where both are offered: it places a picture and can take it back, where
        // a sixel has to be painted over.
        return SixelGraphics.Contains(name) ? "sixel" : "half-blocks";
    }

    private static bool HasTrueColor(Func<string, string?> environment, string name, TerminalSurface surface)
    {
        // A kernel console has the palette the kernel gives it, whatever the environment
        // claims — `COLORTERM` there was exported by a profile, not by the console.
        if (surface is TerminalSurface.Console) { return false; }

        // Everywhere else `COLORTERM` is the terminal declaring what it can do, which beats
        // anything inferred from its name.
        var declared = environment("COLORTERM") ?? string.Empty;
        if (Mentions(declared, "truecolor") || Mentions(declared, "24bit")) { return true; }

        return surface is TerminalSurface.Window && TrueColorTerminals.Contains(name);
    }

    private static int ColorsFor(
        Func<string, string?> environment,
        string name,
        string term,
        TerminalSurface surface)
    {
        if (HasTrueColor(environment, name, surface)) { return 16_777_216; }

        return surface switch
        {
            TerminalSurface.Console => 16,
            TerminalSurface.Basic => Mentions(term, "dumb") ? 2 : 8,
            _ => Mentions(term, "256color") || Mentions(term, "direct") ? 256 : 16,
        };
    }

    /// <summary>The capabilities a terminal of this name has, whatever the environment says.</summary>
    public static TerminalProfile ForName(string name, TerminalProfile detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        var known = (name ?? string.Empty).Trim().ToLowerInvariant();

        // Naming a console says what it is displayed on as well as what it is called.
        var surface = Surfaces.FirstOrDefault(s => s.Name == known).Surface is var found && found != default
            ? found
            : (known is "tmux" or "screen" ? TerminalSurface.Multiplexer : TerminalSurface.Window);

        return ForSurface(known, surface) with
        {
            Program = detected.Program,
            Term = detected.Term,
            Remote = detected.Remote,
        };
    }

    private static bool Mentions(string value, string word)
        => value.Contains(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the reader has said about their terminal, where they did not want it inferred.
/// </summary>
/// <remarks>
/// Every setting is nullable and every null means "work it out". Naming the terminal re-derives
/// the capabilities from that name; setting one capability overrides only it.
/// </remarks>
public sealed class ToshTerminalConfig : IResettableShellConfig
{
    /// <summary>The terminal's short name, such as <c>ghostty</c>. Null detects it.</summary>
    public string? Name { get; set; }

    /// <summary>Whether to send 24-bit colour. Null decides from the terminal.</summary>
    public bool? TrueColor { get; set; }

    /// <summary>How many colours the terminal has. Null decides from the terminal.</summary>
    public int? Colors { get; set; }

    /// <summary>Whether the terminal can draw beyond its console font. Null decides from it.</summary>
    public bool? Unicode { get; set; }

    /// <summary>Whether to ask for unambiguous key reports. Null decides from the terminal.</summary>
    public bool? KittyKeyboard { get; set; }

    /// <summary>Whether to ask for bracketed paste. Null decides from the terminal.</summary>
    public bool? BracketedPaste { get; set; }

    /// <summary>Whether to ask for focus reports. Null decides from the terminal.</summary>
    public bool? FocusReporting { get; set; }

    /// <summary>The picture protocol: <c>kitty</c>, <c>sixel</c>, <c>half-blocks</c> or <c>none</c>.</summary>
    public string? Graphics { get; set; }

    /// <summary>This configuration laid over what was detected.</summary>
    public TerminalProfile Over(TerminalProfile detected)
    {
        ArgumentNullException.ThrowIfNull(detected);

        var profile = string.IsNullOrWhiteSpace(Name)
            ? detected
            : TerminalProfile.ForName(Name, detected);

        return profile with
        {
            TrueColor = TrueColor ?? profile.TrueColor,
            Colors = Colors ?? profile.Colors,
            Unicode = Unicode ?? profile.Unicode,
            KittyKeyboard = KittyKeyboard ?? profile.KittyKeyboard,
            BracketedPaste = BracketedPaste ?? profile.BracketedPaste,
            FocusReporting = FocusReporting ?? profile.FocusReporting,
            Graphics = string.IsNullOrWhiteSpace(Graphics)
                ? profile.Graphics
                : Graphics.Trim().ToLowerInvariant(),
        };
    }

    public void Reset()
    {
        Name = null;
        TrueColor = null;
        Colors = null;
        Unicode = null;
        KittyKeyboard = null;
        BracketedPaste = null;
        FocusReporting = null;
        Graphics = null;
    }
}

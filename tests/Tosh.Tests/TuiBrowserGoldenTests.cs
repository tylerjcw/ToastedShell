using System.Text;
using System.Text.RegularExpressions;
using Tosh.Cli.Tui;
using Tosh.Runtime;
using Tosh.Tui.Requests;
using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Golden masters for the two browser screens, so they can be taken apart safely.
/// </summary>
/// <remarks>
/// <para>
/// <c>HelpBrowserScreen</c> and <c>ConfigBrowserScreen</c> are ~6,800 lines, and they are
/// about to be split up. They are not untested — <see cref="HelpBrowserScreenTests"/> and
/// <see cref="ConfigBrowserScreenTests"/> carry 56 tests between them — but those assert
/// on extracted label lists and detail lines, and only five of them touch <c>Render</c>.
/// Splitting up rendering code is the change that slips past assertions shaped like that,
/// so what is added here is the rendered frame itself.
/// </para>
/// <para>
/// These do not assert the screens are <em>right</em> — nobody has written down what right
/// is — only that they do not change. That is the property a refactor actually needs, and
/// it is cheap here because <see cref="ITuiScreen"/> is already pure: <c>Render</c> returns
/// a string and <c>HandleInput</c> takes a value, so a screen runs with no terminal.
/// </para>
/// <para>
/// The scripts deliberately cover the two regions slated for extraction — the CLR
/// explorer behind <c>F4</c>, and the config value editors behind <c>Space</c>/<c>e</c> —
/// because a safety net that misses the code being moved is not a safety net.
/// </para>
/// <para>
/// A failure here after a refactor means rendered output moved. If that was intended,
/// re-record with <c>TOSH_RERECORD_TUI=1</c> and <em>read the diff</em> before committing.
/// Re-recording without reading is the one way these stop being worth anything.
/// </para>
/// </remarks>
public sealed class TuiBrowserGoldenTests : IDisposable
{
    private static readonly TuiSize Size = new(120, 40);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tosh-tui-golden-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    /// <summary>
    /// A runtime whose config, profile and autoload all point at a scratch directory.
    /// </summary>
    /// <remarks>
    /// The config browser can write: <c>s</c> saves, and the collection editor has a save
    /// action of its own. No script below presses those, but TōSh is the user's login
    /// shell, so "no script presses it" is the wrong level of assurance for a file that
    /// can break their shell. Redirecting the root means even a future script that strays
    /// into a save path writes to a directory this test owns and deletes.
    /// </remarks>
    private ToshRuntime SandboxedRuntime()
    {
        Directory.CreateDirectory(_root);
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Startup.ApplyRootDirectory(_root);
        return runtime;
    }

    private static TuiInputEvent Key(ConsoleKey key, ConsoleModifiers modifiers = 0)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo(
            '\0', key,
            modifiers.HasFlag(ConsoleModifiers.Shift),
            modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control)));

    /// <summary>
    /// A typed character, with the <see cref="ConsoleKey"/> a real terminal would report.
    /// </summary>
    /// <remarks>
    /// This matters more than it looks: both screens dispatch on <c>key.Key</c>, so a
    /// character sent under the wrong key code silently does nothing. An earlier draft of
    /// these tests sent every character as <c>ConsoleKey.A</c> and recorded four snapshots
    /// in which typing changed not one pixel.
    /// </remarks>
    private static TuiInputEvent Type(char ch)
    {
        var key = ch switch
        {
            >= 'a' and <= 'z' => ConsoleKey.A + (ch - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (ch - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (ch - '0'),
            '/' => ConsoleKey.Oem2,
            '.' => ConsoleKey.OemPeriod,
            ',' => ConsoleKey.OemComma,
            '-' => ConsoleKey.OemMinus,
            '[' => ConsoleKey.Oem4,
            ']' => ConsoleKey.Oem6,
            ' ' => ConsoleKey.Spacebar,
            _ => ConsoleKey.Oem1,
        };

        return TuiInputEvent.FromKey(new ConsoleKeyInfo(ch, key, false, false, false));
    }

    private static (string Label, TuiInputEvent Input)[] Typing(string text)
        => text.Select(c => ($"type '{c}'", Type(c))).ToArray();

    /// <summary>
    /// Renders, then drives the script, capturing a frame after every step. Each frame is
    /// labelled so a diff names the keystroke that moved the output.
    /// </summary>
    private static Transcript Drive(ITuiScreen screen, params (string Label, TuiInputEvent Input)[] script)
        => Drive(screen, 0, script);

    /// <summary>
    /// Drives the script, capturing a frame after every step from <paramref name="captureAfter"/>
    /// onwards. Each frame is labelled so a diff names the keystroke that moved the output.
    /// </summary>
    /// <param name="captureAfter">
    /// How many leading steps to run without recording. The CLR scripts use this to skip the
    /// unfiltered view: it lists every loaded assembly, and the rest of the suite emits
    /// assemblies with generated names, so the rows above the fold differ depending on what
    /// else has run. Only the filtered view is stable enough to hold still.
    /// </param>
    private static Transcript Drive(
        ITuiScreen screen,
        int captureAfter,
        params (string Label, TuiInputEvent Input)[] script)
    {
        var sb = new StringBuilder();
        var plain = new StringBuilder();
        var step = 0;

        if (captureAfter == 0)
        {
            sb.Append("─── initial ───\n").Append(Normalize(Paint(screen))).Append('\n');
            plain.Append("─── initial ───\n").Append(Normalize(PlainOf(screen))).Append('\n');
        }

        foreach (var (label, input) in script)
        {
            if (step++ < captureAfter)
            {
                screen.HandleInput(input);
                continue;
            }

            var result = screen.HandleInput(input);
            sb.Append($"─── after {label} → {result} ───\n");
            sb.Append(Normalize(Paint(screen))).Append('\n');
            plain.Append($"─── after {label} → {result} ───\n");
            plain.Append(Normalize(PlainOf(screen))).Append('\n');
            if (result == TuiScreenResult.Exit) break;
        }

        return new Transcript(sb.ToString(), plain.ToString());
    }

    /// <summary>A session recorded twice: as the terminal receives it, and as it reads.</summary>
    private readonly record struct Transcript(string Styled, string Plain);

    /// <summary>
    /// Replaces the parts of a frame that change without anyone editing the code.
    /// </summary>
    /// <remarks>
    /// The config browser renders a live preview of the user's prompt, which embeds the
    /// working directory, the git branch with its dirty marker and ahead count, and
    /// <c>user@host</c>. Left alone that has two consequences: the snapshot fails on the
    /// very next commit when the ahead count moves, and the user's account name gets
    /// committed to a public repository. A net that cries wolf every commit gets
    /// re-recorded without being read, which is the same as having no net.
    /// </remarks>
    private static string Normalize(string frame)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cwd = Directory.GetCurrentDirectory();

        // Longest first, so the tilde-collapsed form is not left half-substituted.
        foreach (var path in new[] { cwd, cwd.Replace(home, "~"), home })
        {
            if (!string.IsNullOrEmpty(path))
                frame = frame.Replace(path, "<path>", StringComparison.Ordinal);
        }

        frame = frame.Replace(Environment.UserName, "<user>", StringComparison.Ordinal);

        // The host name is rendered truncated to fit the pane ("valinor" as "vali…"), so
        // replacing Environment.MachineName literally never matches. Take whatever the
        // prompt put after the "@" instead.
        frame = Regex.Replace(frame, @"<user>@[^\x1b\u2502]*", "<user>@<host>");

        // Everything from the path segment to the end of the line is the prompt preview,
        // and all of it moves on its own: the git branch carries a dirty marker and an
        // ahead count, and the preview also renders how long the last command took — so
        // under full-suite load the same frame grew a "1.4s" segment and re-ordered its
        // right-hand side. Scrubbing the git span alone was not enough; the whole preview
        // has to go. Nothing in these screens is under test here except that the preview
        // is drawn where it is drawn.
        //
        // The escape prefix is optional because the same frame is also recorded with its
        // escapes stripped, and a rule that only matches the styled form leaves the plain
        // twin carrying the git ahead count — which moves on the next commit, which is the
        // thing this rule exists to stop.
        frame = Regex.Replace(
            frame, @"(\x1b\[1;34m)?<path>.*$", "<prompt preview>", RegexOptions.Multiline);

        // A belt-and-braces rule for an elapsed-time segment rendered anywhere else.
        frame = Regex.Replace(frame, @"\b\d+\.\d+(ms|s)\b", "<elapsed>");

        // The CLR explorer lists what is loaded, and much of this suite emits assemblies at
        // run time with a GUID in the name — ImportedTypes_077ed0d2…, ConformanceMatrix_0396…
        // Their names are scrubbed here; their effect on row counts is handled by capturing
        // only the filtered view. See the captureAfter parameter on Drive.
        frame = Regex.Replace(frame, @"\b[A-Za-z_][A-Za-z0-9_]*_[0-9a-f]{12,}\b", "<generated>");

        return frame;
    }

    /// <summary>Keeps only the detail pane of each frame, with styling stripped.</summary>
    /// <remarks>
    /// For the CLR type page the sidebar is not worth asserting on: it lists whatever types
    /// are loaded, which the rest of the suite changes. The detail pane is the part the
    /// extracted builders draw, and for a type reached by name it is the same every run.
    /// The pane starts at a fixed column because <see cref="Size"/> is fixed: the layout
    /// gives the sidebar width/3 columns, clamped to at least 28, then a one-column gap.
    /// </remarks>
    /// <summary>Both halves of a transcript, narrowed to the detail pane.</summary>
    private static Transcript DetailPaneOnly(Transcript transcript)
        => new(DetailPaneOnly(transcript.Styled), DetailPaneOnly(transcript.Plain));

    private static string DetailPaneOnly(string frame)
    {
        const int detailColumn = 42;

        var lines = Regex.Replace(frame, @"\x1b\[[0-9;]*m", string.Empty).Split('\n');
        return string.Join('\n', lines.Select(
            line => line.Length > detailColumn ? line[detailColumn..].TrimEnd() : line.TrimEnd()));
    }

    /// <summary>
    /// A frame as text, whichever form the screen produced it in.
    /// </summary>
    /// <remarks>
    /// A screen that has moved onto widgets answers with a grid of cells and an empty
    /// <c>Content</c>; one that has not answers with a string it built itself. Painting
    /// the grid in full — no diff against a previous frame — is what makes the two
    /// comparable at all, and is what the terminal does for the first frame anyway.
    /// </remarks>
    private static string Paint(ITuiScreen screen)
    {
        var frame = screen.Render(Size);

        return frame.Buffer is { } buffer ? TuiTerminalWriter.Present(buffer) : frame.Content;
    }

    /// <summary>The frame as rows of plain text: what the reader actually sees.</summary>
    /// <remarks>
    /// Taken from the cells when there are cells, because the painted form positions the
    /// cursor instead of emitting newlines and stripping its escapes would run the whole
    /// screen onto one line. A string frame is split on the newlines it wrote itself.
    /// </remarks>
    private static string PlainOf(ITuiScreen screen)
    {
        var frame = screen.Render(Size);

        if (frame.Buffer is not { } buffer)
        {
            return Plain(frame.Content);
        }

        return string.Join('\n', Enumerable
            .Range(0, buffer.Height)
            .Select(row => buffer.RowText(row).TrimEnd()));
    }

    /// <summary>
    /// The same frame with every escape sequence removed: what the reader actually sees.
    /// </summary>
    /// <remarks>
    /// Recorded beside the styled snapshot because the two answer different questions. The
    /// styled one catches a colour that moved; this one catches a character that moved, and
    /// it survives a change in <em>how</em> the escape codes are emitted — which is exactly
    /// what porting a screen from string building to cell painting changes. Without it, a
    /// port has to re-record everything and the net is spent on the one change it was
    /// recorded to watch.
    /// </remarks>
    private static string Plain(string painted)
        => string.Join('\n', Regex
            .Replace(painted, @"\x1b\[[0-9;?]*[a-zA-Z]", string.Empty)
            .Split('\n')
            .Select(line => line.TrimEnd()));

    private void Verify(string name, Transcript transcript)
    {
        VerifyAgainst(Path.Combine("Snapshots", "plain"), name, transcript.Plain);
        VerifyAgainst("Snapshots", name, transcript.Styled);
    }

    private void VerifyAgainst(string folder, string name, string actual)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var directory = Path.Combine(root, "tests", "Tosh.Tests", folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".txt");

        if (Environment.GetEnvironmentVariable("TOSH_RERECORD_TUI") == "1" || !File.Exists(path))
        {
            File.WriteAllText(path, actual);
            return;
        }

        var expected = File.ReadAllText(path);
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;

        // Name the first line that moved: a 40-row frame diffed whole is unreadable.
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var at = 0;
        while (at < e.Length && at < a.Length && e[at] == a[at]) at++;

        Assert.Fail(
            $"{name} rendered differently at line {at + 1} ({folder}).\n"
            + $"  expected: {Escape(at < e.Length ? e[at] : "<end of frame>")}\n"
            + $"  actual:   {Escape(at < a.Length ? a[at] : "<end of frame>")}\n"
            + $"Snapshot: {path}\n"
            + "If the change is intended, re-record with TOSH_RERECORD_TUI=1 and read the diff.");

        static string Escape(string line) => line.Replace("\x1b", "\\e", StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_browser_renders_and_navigates_the_same_way()
    {
        var screen = new HelpBrowserScreen(SandboxedRuntime(), new HelpBrowseRequest(null, null));

        Verify("help-browser", Drive(screen,
            ("down", Key(ConsoleKey.DownArrow)),
            ("down", Key(ConsoleKey.DownArrow)),
            ("right, opening the entry", Key(ConsoleKey.RightArrow)),
            ("down in the detail pane", Key(ConsoleKey.DownArrow)),
            ("tab", Key(ConsoleKey.Tab)),
            ("end", Key(ConsoleKey.End)),
            ("home", Key(ConsoleKey.Home)),
            ("page down", Key(ConsoleKey.PageDown)),
            ("shift+tab", Key(ConsoleKey.Tab, ConsoleModifiers.Shift))));
    }

    [Fact]
    public void The_help_browsers_search_filters_the_same_way()
    {
        var screen = new HelpBrowserScreen(SandboxedRuntime(), new HelpBrowseRequest(null, null));

        // "ping" avoids the letter q on purpose — see
        // The_help_browser_quits_when_q_is_typed_into_its_search_box.
        Verify("help-browser-search", Drive(screen,
            [("/ opens search", Type('/')),
             .. Typing("ping"),
             ("enter", Key(ConsoleKey.Enter)),
             ("down", Key(ConsoleKey.DownArrow)),
             ("right, opening the entry", Key(ConsoleKey.RightArrow))]));
    }

    /// <summary>The CLR explorer — the largest piece slated to move out of the help browser.</summary>
    /// <remarks>
    /// <para>
    /// Both CLR scripts open on a filtered view and record nothing before it. The unfiltered
    /// CLR group lists every loaded assembly, and much of this suite emits assemblies at run
    /// time with generated names, so running the whole suite changes which rows exist, how
    /// they are counted and what order they come in — the snapshot fails on rows that have
    /// nothing to do with the help browser. A query the generated names cannot match leaves
    /// a view made only of the BCL, which holds still.
    /// </para>
    /// <para>
    /// Drilling uses <c>Enter</c>, not <c>RightArrow</c>: right opens the entry and moves
    /// focus to the detail pane, after which every further key scrolls text instead of
    /// walking the type tree.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_clr_explorer_renders_and_drills_down_the_same_way()
    {
        var screen = new HelpBrowserScreen(SandboxedRuntime(), new HelpBrowseRequest(null, null));

        (string, TuiInputEvent)[] openFilteredView =
            [("F4, selecting the CLR group", Key(ConsoleKey.F4)),
             ("/ opens search", Type('/')),
             .. Typing("System.Text"),
             ("enter, leaving search", Key(ConsoleKey.Enter))];

        Verify("help-browser-clr", Drive(screen, openFilteredView.Length,
            [.. openFilteredView,
             ("down", Key(ConsoleKey.DownArrow)),
             ("down", Key(ConsoleKey.DownArrow)),
             ("enter, opening the entry", Key(ConsoleKey.Enter)),
             ("down", Key(ConsoleKey.DownArrow)),
             ("enter", Key(ConsoleKey.Enter)),
             ("page down", Key(ConsoleKey.PageDown)),
             ("left, back up a level", Key(ConsoleKey.LeftArrow)),
             ("tab, into the detail pane", Key(ConsoleKey.Tab))]));
    }

    /// <summary>A rendered CLR type page — members, methods and all.</summary>
    /// <remarks>
    /// Nothing else in this file renders a method page, and a method page is most of what
    /// the CLR explorer's builders exist to draw, so this is the snapshot that would notice
    /// if moving them changed anything.
    /// </remarks>
    [Fact]
    public void The_clr_explorer_renders_a_type_page_the_same_way()
    {
        // Seeded with both a query and a topic name: the constructor applies the filter
        // first and then selects by name, so the selection does not depend on how many
        // rows happen to sit above the type. Counting arrow presses put this script on
        // whichever generated or platform type the row offset happened to hit.
        var screen = new HelpBrowserScreen(
            SandboxedRuntime(),
            new HelpBrowseRequest("System.Text.StringBuilder", "System.Text.StringBuilder"));

        Verify("help-browser-clr-type", DetailPaneOnly(Drive(screen,
            ("enter, opening the type", Key(ConsoleKey.Enter)),
            ("tab, into the detail pane", Key(ConsoleKey.Tab)),
            ("page down", Key(ConsoleKey.PageDown)),
            ("page down", Key(ConsoleKey.PageDown)),
            ("end", Key(ConsoleKey.End)),
            ("home", Key(ConsoleKey.Home)))));
    }

    [Fact]
    public void The_config_browser_renders_and_navigates_the_same_way()
    {
        var screen = new ConfigBrowserScreen(SandboxedRuntime(), new ConfigBrowseRequest(null, null));

        Verify("config-browser", Drive(screen,
            ("down", Key(ConsoleKey.DownArrow)),
            ("down", Key(ConsoleKey.DownArrow)),
            ("enter, expanding the group", Key(ConsoleKey.Enter)),
            ("down", Key(ConsoleKey.DownArrow)),
            ("right into the detail pane", Key(ConsoleKey.RightArrow)),
            ("tab", Key(ConsoleKey.Tab)),
            ("end", Key(ConsoleKey.End)),
            ("home", Key(ConsoleKey.Home)),
            ("page down", Key(ConsoleKey.PageDown)),
            ("left", Key(ConsoleKey.LeftArrow))));
    }

    [Fact]
    public void The_config_browsers_search_filters_the_same_way()
    {
        var screen = new ConfigBrowserScreen(SandboxedRuntime(), new ConfigBrowseRequest(null, null));

        Verify("config-browser-search", Drive(screen,
            [("/ opens search", Type('/')),
             .. Typing("prompt"),
             ("enter", Key(ConsoleKey.Enter)),
             ("down", Key(ConsoleKey.DownArrow)),
             ("down", Key(ConsoleKey.DownArrow))]));
    }

    /// <summary>The value editors — the piece slated to move out of the config browser.</summary>
    /// <remarks>
    /// Nothing here presses <c>s</c> or <c>a</c>: staging is in memory, applying and saving
    /// are not. The runtime is sandboxed as well, so this cannot reach the real config.
    /// </remarks>
    [Fact]
    public void The_config_value_editors_open_and_cancel_the_same_way()
    {
        // Opened directly on a boolean leaf. An earlier draft walked down four rows and
        // landed on a collapsed group, where there is nothing to edit: every editor key
        // was a no-op and the snapshot recorded eleven identical frames.
        var screen = new ConfigBrowserScreen(
            SandboxedRuntime(),
            new ConfigBrowseRequest(null, "Repl.SyntaxHighlightingEnabled"));

        Verify("config-browser-edit", Drive(screen,
            ("space, toggling the boolean", Type(' ')),
            ("space, toggling it back", Type(' ')),
            ("down to the next leaf", Key(ConsoleKey.DownArrow)),
            ("e, opening the editor", Type('e')),
            ("escape, cancelling the edit", Key(ConsoleKey.Escape)),
            ("t, opening the raw editor", Type('t')),
            ("escape, cancelling the raw edit", Key(ConsoleKey.Escape)),
            ("r, reverting the node", Type('r'))));
    }

    /// <summary>A string-valued leaf, which opens a different editor than a boolean.</summary>
    [Fact]
    public void The_config_text_editor_accepts_and_cancels_the_same_way()
    {
        var screen = new ConfigBrowserScreen(
            SandboxedRuntime(),
            new ConfigBrowseRequest(null, "Repl.ContinuationPrompt"));

        Verify("config-browser-text-edit", Drive(screen,
            [("e, opening the editor", Type('e')),
             .. Typing("xy"),
             ("backspace", Key(ConsoleKey.Backspace)),
             ("escape, cancelling", Key(ConsoleKey.Escape))]));
    }

    /// <summary>
    /// Both browsers test for <c>q</c> before they test whether focus is in the search box,
    /// so a search for anything containing that letter quits instead of typing.
    /// </summary>
    /// <remarks>
    /// These two were pinned asserting the wrong answer, because a characterisation suite
    /// exists to hold behaviour still and changing it in the same breath would defeat the
    /// point. The refactor they were guarding is done, so they now assert what a reader
    /// expects: a letter typed into a search box is a letter (<c>TUI-0006</c>).
    /// </remarks>
    [Fact]
    public void The_help_browser_takes_q_as_a_letter_while_its_search_box_has_the_keyboard()
    {
        var screen = new HelpBrowserScreen(SandboxedRuntime(), new HelpBrowseRequest(null, null));

        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Type('/')));
        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Type('s')));
        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Type('q')));

        Assert.Contains("sq", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_browser_still_quits_on_q_when_the_search_box_does_not_have_it()
    {
        var screen = new HelpBrowserScreen(SandboxedRuntime(), new HelpBrowseRequest(null, null));

        Assert.Equal(TuiScreenResult.Exit, screen.HandleInput(Type('q')));
    }

    [Fact]
    public void Escape_leaves_the_help_browsers_search_box_rather_than_the_browser()
    {
        // The branch this reaches was unreachable while Escape quit outright.
        var screen = new HelpBrowserScreen(SandboxedRuntime(), new HelpBrowseRequest(null, null));

        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Type('/')));
        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Key(ConsoleKey.Escape)));

        // Out of the search box, so the browser's own keys answer again.
        Assert.Equal(TuiScreenResult.Exit, screen.HandleInput(Type('q')));
    }

    [Fact]
    public void The_config_browser_takes_q_as_a_letter_while_its_search_box_has_the_keyboard()
    {
        var screen = new ConfigBrowserScreen(SandboxedRuntime(), new ConfigBrowseRequest(null, null));

        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Type('/')));
        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Type('s')));
        Assert.Equal(TuiScreenResult.Continue, screen.HandleInput(Type('q')));

        Assert.Contains("sq", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void The_config_browser_still_quits_on_q_when_the_search_box_does_not_have_it()
    {
        var screen = new ConfigBrowserScreen(SandboxedRuntime(), new ConfigBrowseRequest(null, null));

        Assert.Equal(TuiScreenResult.Exit, screen.HandleInput(Type('q')));
    }

    /// <summary>The whole frame as plain text, for asserting that a query reached the box.</summary>
    private static string Frame(ITuiScreen screen) => Normalize(PlainOf(screen));
}

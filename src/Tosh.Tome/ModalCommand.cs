using System.Diagnostics;
using System.Text;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Modal command surface for Tōme. Command mode (vim-ish normal mode) plus
/// the ':' command palette live here so TomeApp.cs stays focused on the
/// editing/rendering core.
/// </summary>
internal sealed partial class TomeApp
{
    internal enum EditorMode { Edit, Command }

    // Command-prompt history. Cycled with Up/Down inside the ':' prompt.
    // Bounded to avoid unbounded growth across long sessions.
    private const int CommandHistoryLimit = 256;
    private readonly List<string> _commandHistory = new();

    // The "*Output*" scratch tab is reused for every shell-bridge invocation
    // so it doesn't accumulate. Tracked by reference rather than by name so
    // a user-opened file named "*Output*" can't accidentally collide.
    private Tab? _outputTab;

    private void HandleCommandModeKey(ConsoleKeyInfo key)
    {
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;

        if (_codeActionsOpen && HandleCodeActionKey(key)) return;

        // Universal nav (arrows, Home/End, PgUp/PgDn) keeps working in Command
        // mode — there's no good reason to disable it.
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: ApplyMove(_buffer.MoveLeft, shift); return;
            case ConsoleKey.RightArrow: ApplyMove(_buffer.MoveRight, shift); return;
            case ConsoleKey.UpArrow: ApplyMove(_buffer.MoveUp, shift); return;
            case ConsoleKey.DownArrow: ApplyMove(_buffer.MoveDown, shift); return;
            case ConsoleKey.Home: ApplyMove(_buffer.MoveLineStart, shift); return;
            case ConsoleKey.End: ApplyMove(_buffer.MoveLineEnd, shift); return;
            case ConsoleKey.PageUp: ApplyMove(() => PageBy(-_view.ViewportHeight), shift); return;
            case ConsoleKey.PageDown: ApplyMove(() => PageBy(_view.ViewportHeight), shift); return;
            case ConsoleKey.Escape: ClearPending(); return; // also clears any pending operator
        }

        if (alt && key.Key == ConsoleKey.OemPeriod) { OpenCodeActions(); return; }

        // Ctrl+R = redo (vim convention); Ctrl+S/Q/etc still useful here.
        if (ctrl)
        {
            switch (key.Key)
            {
                case ConsoleKey.R: _buffer.Redo(); return;
                case ConsoleKey.S: Save(); return;
                case ConsoleKey.Q: TryQuit(); return;
            }
        }

        // Operator+motion+text-object state machine. Consumes multi-key
        // sequences (dd, ciw, gg, f<c>, etc.). When it returns false the
        // key falls through to the single-key motion/mode table below.
        if (TryHandleMotionOrOperator(key)) return;

        switch (key.KeyChar)
        {
            case 'h': ApplyMove(_buffer.MoveLeft, shift); return;
            case 'j': ApplyMove(_buffer.MoveDown, shift); return;
            case 'k': ApplyMove(_buffer.MoveUp, shift); return;
            case 'l': ApplyMove(_buffer.MoveRight, shift); return;
            case '0': ApplyMove(_buffer.MoveLineStart, shift); return;
            case '$': ApplyMove(_buffer.MoveLineEnd, shift); return;
            case 'w': ApplyMove(_buffer.MoveWordRight, shift); return;
            case 'b': ApplyMove(_buffer.MoveWordLeft, shift); return;
            case 'G': _buffer.MoveCursor(new TextLocation(_buffer.LineCount - 1, 0)); return;
            case 'x':
                if (_buffer.HasSelection) _buffer.DeleteSelection();
                else _buffer.DeleteForward();
                return;
            case 'u': _buffer.Undo(); return;

            // Insert-mode entries.
            case 'i': EnterEditMode(); return;
            case 'a': _buffer.MoveRight(); EnterEditMode(); return;
            case 'I': _buffer.MoveLineStart(); EnterEditMode(); return;
            case 'A': _buffer.MoveLineEnd(); EnterEditMode(); return;
            case 'o': _buffer.MoveLineEnd(); _buffer.InsertNewline(); EnterEditMode(); return;
            case 'O':
                _buffer.MoveLineStart();
                _buffer.InsertNewline();
                _buffer.MoveUp();
                EnterEditMode();
                return;

            case ':': OpenCommandPrompt(prefill: string.Empty); return;
            case '!': OpenCommandPrompt(prefill: "!"); return;
            case '/': StartSearch(); return;
            case 'n': FindNext(); return;
            case 'K': ShowHover(); return;
        }
    }

    private void EnterEditMode()
    {
        _mode = EditorMode.Edit;
        _message = "-- EDIT --";
    }

    // ─── Command prompt (':') ────────────────────────────────────────────

    private void OpenCommandPrompt(string prefill)
    {
        var input = PromptCommand(prefill);
        if (input is null) { _message = string.Empty; return; }
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return;

        // De-dup the very last history entry to avoid spam from repeated cmds.
        if (_commandHistory.Count == 0 || !string.Equals(_commandHistory[^1], trimmed, StringComparison.Ordinal))
        {
            _commandHistory.Add(trimmed);
            if (_commandHistory.Count > CommandHistoryLimit)
                _commandHistory.RemoveAt(0);
        }

        DispatchCommand(trimmed);
    }

    /// <summary>
    /// One-line command prompt with Up/Down history navigation and Tab
    /// completion against the verb table (when at the first token) or
    /// filesystem paths (when the verb expects a path).
    /// </summary>
    private string? PromptCommand(string prefill)
    {
        var input = new StringBuilder(prefill);
        var historyIndex = _commandHistory.Count;   // points "past the end"
        string? stashedDraft = null;

        while (true)
        {
            DrawPrompt(":", input.ToString());
            var key = _terminal.ReadKey();

            if (key.Key == ConsoleKey.Escape) return null;
            if (key.Key == ConsoleKey.Enter) return input.ToString();

            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0) input.Length--;
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                if (_commandHistory.Count == 0) continue;
                if (historyIndex == _commandHistory.Count) stashedDraft = input.ToString();
                if (historyIndex > 0) historyIndex--;
                input.Clear();
                input.Append(_commandHistory[historyIndex]);
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (_commandHistory.Count == 0) continue;
                if (historyIndex < _commandHistory.Count) historyIndex++;
                input.Clear();
                if (historyIndex == _commandHistory.Count)
                    input.Append(stashedDraft ?? string.Empty);
                else
                    input.Append(_commandHistory[historyIndex]);
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                var current = input.ToString();
                var completed = TryCompleteCommand(current);
                if (completed is not null)
                {
                    input.Clear();
                    input.Append(completed);
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
        }
    }

    private string? TryCompleteCommand(string current)
    {
        // First token: complete against verb names. Subsequent tokens:
        // delegate to filesystem completion when the verb is path-taking.
        var spaceIdx = current.IndexOf(' ');
        if (spaceIdx < 0)
        {
            var matches = EditorVerbs
                .Where(v => v.StartsWith(current, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length == 1) return matches[0] + " ";
            return LongestCommonPrefixOf(matches);
        }

        var verb = current[..spaceIdx];
        var rest = current[(spaceIdx + 1)..];
        if (!PathTakingVerbs.Contains(verb)) return null;
        var completedPath = TryCompletePath(rest);
        return completedPath is null ? null : verb + " " + completedPath;
    }

    private static string? LongestCommonPrefixOf(string[] entries)
    {
        if (entries.Length == 0) return null;
        var prefix = entries[0];
        for (var i = 1; i < entries.Length; i++)
        {
            var s = entries[i];
            var n = Math.Min(prefix.Length, s.Length);
            var k = 0;
            while (k < n && prefix[k] == s[k]) k++;
            prefix = prefix[..k];
            if (prefix.Length == 0) break;
        }
        return prefix.Length == 0 ? null : prefix;
    }

    // ─── Dispatcher + verb table ─────────────────────────────────────────

    private static readonly HashSet<string> PathTakingVerbs = new(StringComparer.Ordinal)
    {
        "e", "edit", "w", "write", "wa", "wq", "saveas",
    };

    private static readonly string[] EditorVerbs =
    {
        "w", "write", "wq", "x",
        "q", "quit", "q!",
        "e", "edit",
        "tabnew", "tabclose", "tabnext", "tabprev", "tn", "tp", "tc", "bd",
        "goto", "g",
        "diag", "d",
        "set",
        "help", "h",
        "mode",
        "workspace", "ws",
        "s", "sub", "substitute",
        "grep", "rg",
        "gsub",
        "carets", "cursors",
        "break",
        "git",
    };

    private void DispatchCommand(string command)
    {
        // Explicit shell escape: ':!cmd' or starting with '!'. Strip the
        // bang and route to the shell bridge directly.
        if (command.StartsWith('!'))
        {
            RunShellBridge(command[1..].Trim());
            return;
        }

        // ':s/pat/repl/flags' and ':gsub/pat/repl/flags' — the slash sticks
        // to the verb, so detect these before the space-tokenizer.
        if (command.Length >= 2 && command[0] == 's' && !char.IsLetterOrDigit(command[1]))
        {
            SubstituteCommand(command[1..]);
            return;
        }
        if (command.StartsWith("gsub", StringComparison.Ordinal)
            && command.Length >= 5 && !char.IsLetterOrDigit(command[4]))
        {
            GsubCommand(command[4..]);
            return;
        }

        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (verb)
        {
            case "w":
            case "write":
                if (!string.IsNullOrEmpty(arg)) Current.FilePath = Path.GetFullPath(arg);
                Save();
                return;
            case "wq":
            case "x":
                if (!string.IsNullOrEmpty(arg)) Current.FilePath = Path.GetFullPath(arg);
                Save();
                if (!_buffer.IsModified) _quit = true;
                return;
            case "q":
            case "quit":
                TryQuit();
                return;
            case "q!":
                _quit = true;
                return;
            case "e":
            case "edit":
                if (string.IsNullOrEmpty(arg)) { _message = "edit: path required"; return; }
                OpenPathDirect(arg);
                return;
            case "tabnew":
                NewTab();
                return;
            case "tabclose":
            case "tc":
            case "bd":
                CloseTab();
                return;
            case "tabnext":
            case "tn":
                SwitchTab(+1);
                return;
            case "tabprev":
            case "tp":
                SwitchTab(-1);
                return;
            case "goto":
            case "g":
                if (string.IsNullOrEmpty(arg)) { GotoLine(); return; }
                GotoLineDirect(arg);
                return;
            case "diag":
            case "d":
                ShowDiagnostics();
                return;
            case "help":
            case "h":
                _message = string.IsNullOrEmpty(arg)
                    ? "verbs: w q wq e tabnew tabclose tc tn tp goto diag set mode break  |  prefix '!' for shell"
                    : $"help: {arg} (not yet implemented — use Ctrl+K for symbol hover)";
                return;
            case "set":
                HandleSet(arg);
                return;
            case "break":
                ToggleBreakpoint(arg);
                return;
            case "mode":
                if (arg == "edit") { EnterEditMode(); return; }
                if (arg == "command") { _mode = EditorMode.Command; _message = "-- COMMAND --"; return; }
                _message = "mode: 'edit' or 'command'";
                return;
            case "workspace":
            case "ws":
                HandleWorkspaceVerb(arg);
                return;
            case "sub":
            case "substitute":
                SubstituteCommand(arg);
                return;
            case "grep":
            case "rg":
                GrepCommand(arg);
                return;
            case "find":
            case "f":
                FindCommand(arg);
                return;
            case "carets":
            case "cursors":
                CaretsCommand(arg);
                return;
            case "repl":
                HandleReplVerb(arg);
                return;
            case "fmt":
            case "format":
                FormatCurrentBuffer();
                return;
            case "files":
            case "p":
            case "fuzzy":
                OpenFuzzyPicker();
                return;
            case "reload":
            case "e!":
                ReloadFromDisk(silent: false);
                return;
            case "git":
                HandleGit(arg);
                return;
            default:
                // Unknown editor verb → fall through to the shell bridge.
                // ':ls' just runs the tosh ls builtin.
                RunShellBridge(command);
                return;
        }
    }

    private void OpenPathDirect(string path)
    {
        var resolved = Path.GetFullPath(path.Trim());
        string text;
        try
        {
            text = File.Exists(resolved) ? File.ReadAllText(resolved) : string.Empty;
        }
        catch (Exception ex)
        {
            _message = $"edit failed: {ex.Message}";
            return;
        }
        var openedTab = new Tab(resolved, text, null);
        openedTab.Colorizer = ResolveColorizer(openedTab);
        _tabs.Add(openedTab);
        _active = _tabs.Count - 1;
        _message = File.Exists(resolved) ? $"opened {resolved}" : $"new file: {resolved}";
    }

    private void GotoLineDirect(string arg)
    {
        var parts = arg.Split(':', 2);
        if (!int.TryParse(parts[0], out var line) || line < 1)
        {
            _message = $"bad line number: {parts[0]}";
            return;
        }
        var col = 1;
        if (parts.Length == 2 && !int.TryParse(parts[1], out col)) col = 1;
        var li = Math.Min(line - 1, _buffer.LineCount - 1);
        var ci = Math.Max(0, Math.Min(col - 1, _buffer.GetLineLength(li)));
        _buffer.ClearSelection();
        _buffer.MoveCursor(new TextLocation(li, ci));
        _message = $"jumped to {li + 1}:{ci + 1}";
    }

    // ─── Embedded REPL pane ──────────────────────────────────────────────

    private void HandleReplVerb(string arg)
    {
        var sub = string.IsNullOrEmpty(arg) ? "toggle" : arg.Trim();
        var cwd = !string.IsNullOrEmpty(Current.FilePath)
            ? Path.GetDirectoryName(Path.GetFullPath(Current.FilePath))
            : Environment.CurrentDirectory;
        switch (sub)
        {
            case "open":
                _repl.Open(cwd);
                _focusRepl = true;
                _message = "-- REPL --";
                return;
            case "close":
            case "hide":
                _repl.Close();
                _focusRepl = false;
                _message = "repl closed";
                return;
            case "toggle":
                _repl.Toggle(cwd);
                _focusRepl = _repl.Visible;
                _message = _repl.Visible ? "-- REPL --" : "repl closed";
                return;
            case "focus":
                if (!_repl.Visible) _repl.Open(cwd);
                _focusRepl = true;
                _message = "-- REPL --";
                return;
            default:
                // ":repl <N>" — set pane height to N rows.
                if (int.TryParse(sub, out var h) && h >= 4)
                {
                    _repl.Height = h;
                    if (!_repl.Visible) _repl.Open(cwd);
                    _message = $"repl height = {h}";
                    return;
                }
                _message = "repl: open | close | toggle | focus | <rows>";
                return;
        }
    }

    // ─── Formatter dispatch ──────────────────────────────────────────────

    private void ToggleBreakpoint(string arg)
    {
        int line;
        if (string.IsNullOrWhiteSpace(arg))
        {
            line = _buffer.Cursor.Line;
        }
        else if (int.TryParse(arg.Trim(), out var oneBased) && oneBased >= 1 && oneBased <= _buffer.LineCount)
        {
            line = oneBased - 1;
        }
        else
        {
            _message = $"break: invalid line '{arg}'";
            return;
        }

        if (!Current.Breakpoints.Add(line))
        {
            Current.Breakpoints.Remove(line);
            _message = $"breakpoint removed at line {line + 1}";
        }
        else
        {
            _message = $"breakpoint set at line {line + 1}";
        }
    }

    private void HandleSet(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { _message = "set: <option> [value]"; return; }
        var name = parts[0].ToLowerInvariant();
        var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (name)
        {
            case "format-on-save":
            case "fmt-on-save":
                _formatOnSave = ParseBool(value, _formatOnSave);
                _settings.FormatOnSave = _formatOnSave;
                _message = _settings.Save() ?? $"format-on-save = {(_formatOnSave ? "on" : "off")}";
                return;
            default:
                _message = $"set: unknown option '{name}'";
                return;
        }
    }

    private static bool ParseBool(string s, bool fallback) => s.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1" => true,
        "off" or "false" or "no" or "0" => false,
        _ => fallback,
    };

    private void FormatCurrentBuffer()
    {
        var path = Current.FilePath;
        if (!Formatter.HasFormatterFor(path))
        {
            var ext = string.IsNullOrEmpty(path) ? "(untitled)" : Path.GetExtension(path);
            _message = $"fmt: no formatter for {ext}";
            return;
        }
        var original = _buffer.GetText();
        var result = Formatter.Format(path, original);
        if (!result.Ok)
        {
            _message = result.Message;
            return;
        }
        if (string.Equals(result.Text, original, StringComparison.Ordinal))
        {
            _message = $"{result.Message} (no changes)";
            return;
        }
        // Preserve cursor when possible; ReplaceAll keeps undo history intact.
        var savedCursor = _buffer.Cursor;
        _buffer.ReplaceAll(result.Text);
        _buffer.MoveCursor(savedCursor);
        _message = result.Message;
    }

    // ─── Shell bridge → *Output* tab ─────────────────────────────────────

    private void RunShellBridge(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) { _message = "shell: nothing to run"; return; }
        var toshPath = ResolveToshBinary();
        if (toshPath is null) { _message = "shell: 'tosh' binary not found on PATH"; return; }

        string stdout, stderr;
        int exitCode;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = toshPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
            exitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            _message = $"shell failed: {ex.Message}";
            return;
        }

        var body = new StringBuilder();
        body.Append("$ ").AppendLine(command);
        if (stdout.Length > 0) body.Append(stdout);
        if (stderr.Length > 0)
        {
            if (body.Length > 0 && body[^1] != '\n') body.AppendLine();
            body.AppendLine("─── stderr ───");
            body.Append(stderr);
        }
        if (body.Length > 0 && body[^1] != '\n') body.AppendLine();
        body.Append($"[exit {exitCode}]").AppendLine();

        ShowOutput(body.ToString());
        _message = $"shell: exit {exitCode}";
    }

    private void ShowOutput(string text)
    {
        if (_outputTab is null || !_tabs.Contains(_outputTab))
        {
            _outputTab = new Tab("*Output*", text, colorizer: null);
            _tabs.Add(_outputTab);
        }
        else
        {
            _outputTab.Buffer.LoadText(text);
            _outputTab.Buffer.MarkClean();
        }
        _active = _tabs.IndexOf(_outputTab);
        // Drop into Command mode in the output tab so the user can immediately
        // ':bd' it or scroll without typing into the transcript.
        _mode = EditorMode.Command;
    }

    private static string? ResolveToshBinary()
    {
        // Prefer a sibling 'tosh' next to the running tome binary so the dev
        // workflow (./artifacts/.../tome alongside tosh) just works. Fall
        // back to PATH lookup.
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "tosh.exe" : "tosh");
                if (File.Exists(sibling)) return sibling;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        var exeName = OperatingSystem.IsWindows() ? "tosh.exe" : "tosh";
        foreach (var entry in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(entry)) continue;
            var candidate = Path.Combine(entry, exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

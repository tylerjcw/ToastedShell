using System.Text;
using System.Text.RegularExpressions;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Search-and-replace and find-in-files. The replace engine is shared
/// by the interactive Ctrl+R prompt, the <c>:s</c> command verb, and the
/// cross-file <c>:gsub</c> verb so they all interpret flags identically.
/// </summary>
internal sealed partial class TomeApp
{
    // Shared scratch tab for *Results* (cross-file grep). Reused across
    // invocations so it doesn't accumulate; tracked by reference so a
    // user-opened "*Results*" file can't accidentally collide.
    private Tab? _resultsTab;

    // ─── Replace flags ───────────────────────────────────────────────────

    private readonly record struct ReplaceFlags(
        bool All,        // 'g' — every match on a line / in a file (default: first only)
        bool IgnoreCase, // 'i'
        bool Regex,      // 'e' (regex literal; default is plain text)
        bool Confirm)    // 'c' — interactive y/n/a/q per match
    {
        public static ReplaceFlags Parse(string s)
        {
            var all = false; var ic = false; var rx = false; var c = false;
            foreach (var ch in s ?? string.Empty)
            {
                switch (ch)
                {
                    case 'g': all = true; break;
                    case 'i': ic = true; break;
                    case 'e': rx = true; break;
                    case 'c': c = true; break;
                }
            }
            return new ReplaceFlags(all, ic, rx, c);
        }
    }

    // Parses ':s/pat/repl/flags' (or any single-char separator after 's').
    // Returns null when malformed. Empty repl is allowed (deletion).
    private static (string Pattern, string Replacement, string Flags)? ParseSubstitution(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return null;
        var sep = arg[0];
        // Find the next two unescaped separators.
        var i = 1;
        var pat = new StringBuilder();
        while (i < arg.Length && arg[i] != sep)
        {
            if (arg[i] == '\\' && i + 1 < arg.Length) { pat.Append(arg[i + 1]); i += 2; continue; }
            pat.Append(arg[i++]);
        }
        if (i >= arg.Length) return (pat.ToString(), string.Empty, string.Empty);
        i++; // consume sep
        var repl = new StringBuilder();
        while (i < arg.Length && arg[i] != sep)
        {
            if (arg[i] == '\\' && i + 1 < arg.Length) { repl.Append(arg[i + 1]); i += 2; continue; }
            repl.Append(arg[i++]);
        }
        var flags = i < arg.Length ? arg[(i + 1)..] : string.Empty;
        return (pat.ToString(), repl.ToString(), flags);
    }

    private static Regex? CompileSearchRegex(string pattern, ReplaceFlags flags, out string? error)
    {
        error = null;
        try
        {
            var opts = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (flags.IgnoreCase) opts |= RegexOptions.IgnoreCase;
            var rx = flags.Regex
                ? new Regex(pattern, opts)
                : new Regex(Regex.Escape(pattern), opts);
            return rx;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ─── ':s' — substitute in current buffer ─────────────────────────────

    private void SubstituteCommand(string arg)
    {
        var parsed = ParseSubstitution(arg);
        if (parsed is null) { _message = "usage: :s/pat/repl/[flags]  (flags: g i e c)"; return; }
        var (pat, repl, flagStr) = parsed.Value;
        if (string.IsNullOrEmpty(pat)) { _message = "substitute: empty pattern"; return; }

        var flags = ReplaceFlags.Parse(flagStr);
        var rx = CompileSearchRegex(pat, flags, out var err);
        if (rx is null) { _message = $"bad pattern: {err}"; return; }

        if (flags.Confirm)
        {
            var count = InteractiveReplace(_buffer, rx, repl, flags);
            _message = count < 0 ? "replace cancelled" : $"replaced {count} occurrence(s)";
            Current.LastSearch = pat;
            return;
        }

        var replaced = ReplaceInBuffer(_buffer, rx, repl, flags);
        Current.LastSearch = pat;
        _message = $"replaced {replaced} occurrence(s)";
    }

    /// <summary>
    /// Non-interactive replace across the entire buffer. Honors the 'g'
    /// flag (per-line all vs. first match). Returns the number of
    /// substitutions performed.
    /// </summary>
    private static int ReplaceInBuffer(TextBuffer buffer, Regex rx, string repl, ReplaceFlags flags)
    {
        var text = buffer.GetText();
        int count;
        string rebuilt;
        if (flags.All)
        {
            count = 0;
            rebuilt = rx.Replace(text, m => { count++; return repl; });
        }
        else
        {
            // Default: one substitution per line, matching vim's ':%s' minus 'g'.
            var lines = text.Replace("\r\n", "\n").Split('\n');
            count = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var m = rx.Match(lines[i]);
                if (!m.Success) continue;
                lines[i] = lines[i][..m.Index] + repl + lines[i][(m.Index + m.Length)..];
                count++;
            }
            rebuilt = string.Join('\n', lines);
        }
        if (count > 0) buffer.LoadText(rebuilt);
        return count;
    }

    /// <summary>
    /// Walks matches one at a time, prompting y/n/a/q for each.
    /// Returns the count applied, or -1 if the user cancelled before
    /// answering anything.
    /// </summary>
    private int InteractiveReplace(TextBuffer buffer, Regex rx, string repl, ReplaceFlags flags)
    {
        var applied = 0;
        var stopAsked = false;
        var acceptAll = false;
        var lineIdx = 0;

        while (lineIdx < buffer.LineCount && !stopAsked)
        {
            var line = buffer.GetLine(lineIdx);
            var col = 0;
            while (col <= line.Length)
            {
                var m = rx.Match(line, col);
                if (!m.Success) break;

                buffer.MoveCursor(new TextLocation(lineIdx, m.Index));
                Render();
                DrawPrompt($"replace [y/n/a/q]? ", $"\"{m.Value}\" → \"{repl}\"");

                bool apply;
                if (acceptAll) apply = true;
                else
                {
                    var key = _terminal.ReadKey();
                    switch (key.KeyChar)
                    {
                        case 'y': case 'Y': apply = true; break;
                        case 'a': case 'A': apply = true; acceptAll = true; break;
                        case 'q':
                        case 'Q':
                        case (char)27: stopAsked = true; apply = false; break;
                        default: apply = false; break;
                    }
                }

                if (apply)
                {
                    var newLine = line[..m.Index] + repl + line[(m.Index + m.Length)..];
                    // Rebuild via LoadText so undo history captures one bulk edit.
                    var allLines = SplitLines(buffer.GetText());
                    allLines[lineIdx] = newLine;
                    buffer.LoadText(string.Join('\n', allLines));
                    line = newLine;
                    col = m.Index + repl.Length;
                    applied++;
                    if (!flags.All) break; // one per line in non-'g' mode
                }
                else
                {
                    col = m.Index + Math.Max(1, m.Length);
                }
                if (stopAsked) break;
            }
            lineIdx++;
        }
        return applied;
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Split('\n');

    // ─── Ctrl+R — interactive find/replace prompt ────────────────────────

    private void StartInteractiveReplace()
    {
        var pat = PromptText("find: ");
        if (string.IsNullOrEmpty(pat)) { _message = "replace cancelled"; return; }
        var repl = PromptText($"replace \"{pat}\" with: ") ?? string.Empty;
        var flagStr = PromptText("flags (g i e c, empty = first-per-line, plain text): ") ?? string.Empty;
        var flags = ReplaceFlags.Parse(flagStr);
        var rx = CompileSearchRegex(pat, flags, out var err);
        if (rx is null) { _message = $"bad pattern: {err}"; return; }
        Current.LastSearch = pat;

        if (flags.Confirm)
        {
            var n = InteractiveReplace(_buffer, rx, repl, flags);
            _message = $"replaced {n} occurrence(s)";
        }
        else
        {
            var n = ReplaceInBuffer(_buffer, rx, repl, flags);
            _message = $"replaced {n} occurrence(s)";
        }
    }

    // ─── ':grep' — find-in-files ─────────────────────────────────────────

    private void GrepCommand(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) { _message = "usage: :grep [/flags] <pattern>"; return; }

        var (flags, pattern) = ParseFlagsPrefix(arg);
        if (string.IsNullOrEmpty(pattern)) { _message = "grep: empty pattern"; return; }
        var rx = CompileSearchRegex(pattern, flags, out var err);
        if (rx is null) { _message = $"bad pattern: {err}"; return; }

        var roots = ResolveSearchRoots();
        if (roots.Count == 0) { _message = "grep: nowhere to search"; return; }

        var results = new List<(string Path, int Line, int Col, string Text)>();
        var scanned = 0;
        foreach (var root in roots) WalkAndGrep(root, rx, results, ref scanned);

        var body = BuildGrepReport(pattern, flags, results, scanned);
        ShowResults(body);
        _message = $"grep: {results.Count} match(es) in {scanned} file(s)";
    }

    // ─── ':find' — filename search across workspace roots ────────────────

    private void FindCommand(string arg)
    {
        var pattern = arg.Trim();
        if (string.IsNullOrEmpty(pattern)) { _message = "usage: :find <name-or-glob>"; return; }

        var roots = ResolveSearchRoots();
        if (roots.Count == 0) { _message = "find: nowhere to search"; return; }

        // Treat a pattern with '*' or '?' as a glob over the filename only;
        // anything else is a case-insensitive substring match.
        var isGlob = pattern.Contains('*') || pattern.Contains('?');
        Regex? rx = null;
        if (isGlob)
        {
            var rxPat = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try { rx = new Regex(rxPat, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (Exception ex) { _message = $"bad glob: {ex.Message}"; return; }
        }

        var matches = new List<string>();
        var scanned = 0;
        foreach (var root in roots) WalkAndFind(root, pattern, rx, matches, ref scanned);

        matches.Sort(StringComparer.Ordinal);
        var body = BuildFindReport(pattern, matches, scanned);
        ShowResults(body);
        _message = $"find: {matches.Count} file(s) of {scanned} scanned";
    }

    private void WalkAndFind(
        string dir,
        string substring,
        Regex? rx,
        List<string> matches,
        ref int scanned)
    {
        var workspaceExcludes = _workspace?.Exclude ?? Array.Empty<string>();
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name)) continue;
            if (AlwaysSkipDirs.Contains(name)) continue;
            if (workspaceExcludes.Contains(name, StringComparer.Ordinal)) continue;

            if (Directory.Exists(entry))
            {
                WalkAndFind(entry, substring, rx, matches, ref scanned);
                continue;
            }
            scanned++;
            var hit = rx is not null
                ? rx.IsMatch(name)
                : name.Contains(substring, StringComparison.OrdinalIgnoreCase);
            if (hit) matches.Add(entry);
        }
    }

    private static string BuildFindReport(string pattern, List<string> matches, int scanned)
    {
        var sb = new StringBuilder();
        sb.Append("find: ").Append(pattern).AppendLine();
        sb.Append("files scanned: ").Append(scanned).Append("  matches: ").Append(matches.Count).AppendLine();
        sb.AppendLine("(press Enter on a path to open)");
        sb.AppendLine();
        foreach (var m in matches) sb.AppendLine(m);
        return sb.ToString();
    }

    // Parses a leading '/flags ' segment (e.g. "/ie foo"). Returns
    // ReplaceFlags + remaining pattern. If no leading '/', flags are empty.
    private static (ReplaceFlags Flags, string Pattern) ParseFlagsPrefix(string arg)
    {
        arg = arg.Trim();
        if (arg.Length == 0 || arg[0] != '/') return (default, arg);
        var sp = arg.IndexOf(' ');
        if (sp < 0) return (default, arg);
        var flagStr = arg[1..sp];
        return (ReplaceFlags.Parse(flagStr), arg[(sp + 1)..].Trim());
    }

    private List<string> ResolveSearchRoots()
    {
        var roots = new List<string>();
        if (_workspace is not null)
        {
            foreach (var f in _workspace.Folders)
                if (Directory.Exists(f.Path)) roots.Add(f.Path);
        }
        if (roots.Count == 0)
        {
            var cwd = Environment.CurrentDirectory;
            if (Directory.Exists(cwd)) roots.Add(cwd);
        }
        return roots;
    }

    private static readonly HashSet<string> AlwaysSkipDirs = new(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "target",
    };

    private void WalkAndGrep(
        string dir,
        Regex rx,
        List<(string Path, int Line, int Col, string Text)> results,
        ref int scanned)
    {
        var workspaceExcludes = _workspace?.Exclude ?? Array.Empty<string>();
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name)) continue;
            if (AlwaysSkipDirs.Contains(name)) continue;
            if (workspaceExcludes.Contains(name, StringComparer.Ordinal)) continue;

            if (Directory.Exists(entry))
            {
                WalkAndGrep(entry, rx, results, ref scanned);
                continue;
            }

            if (!IsLikelyTextFile(entry)) continue;
            scanned++;

            string[] lines;
            try { lines = File.ReadAllLines(entry); }
            catch { continue; }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var m = rx.Match(line);
                if (!m.Success) continue;
                results.Add((entry, i + 1, m.Index + 1, line));
            }
        }
    }

    private static bool IsLikelyTextFile(string path)
    {
        // Cheap heuristic: skip files larger than 4 MB, and sniff the first
        // 4 KB for NUL bytes. Saves us from grep-ing binaries by accident.
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > 4L * 1024 * 1024) return false;
            using var fs = fi.OpenRead();
            Span<byte> buf = stackalloc byte[Math.Min(4096, (int)fi.Length)];
            var read = fs.Read(buf);
            for (var i = 0; i < read; i++) if (buf[i] == 0) return false;
            return true;
        }
        catch { return false; }
    }

    private static string BuildGrepReport(
        string pattern,
        ReplaceFlags flags,
        List<(string Path, int Line, int Col, string Text)> results,
        int scanned)
    {
        var sb = new StringBuilder();
        var flagDesc = new StringBuilder();
        if (flags.Regex) flagDesc.Append('e');
        if (flags.IgnoreCase) flagDesc.Append('i');
        sb.Append("grep: ").Append(pattern);
        if (flagDesc.Length > 0) sb.Append(" [").Append(flagDesc).Append(']');
        sb.AppendLine();
        sb.Append("files scanned: ").Append(scanned).Append("  matches: ").Append(results.Count).AppendLine();
        sb.AppendLine("(press Enter on a match line to jump)");
        sb.AppendLine();

        string? lastPath = null;
        foreach (var r in results)
        {
            if (r.Path != lastPath)
            {
                if (lastPath is not null) sb.AppendLine();
                sb.Append(r.Path).AppendLine();
                lastPath = r.Path;
            }
            sb.Append("  ").Append(r.Line).Append(':').Append(r.Col).Append("  ").Append(r.Text.TrimEnd()).AppendLine();
        }
        return sb.ToString();
    }

    private void ShowResults(string text)
    {
        if (_resultsTab is null || !_tabs.Contains(_resultsTab))
        {
            _resultsTab = new Tab("*Results*", text, colorizer: null);
            _tabs.Add(_resultsTab);
        }
        else
        {
            _resultsTab.Buffer.LoadText(text);
            _resultsTab.Buffer.MarkClean();
        }
        _active = _tabs.IndexOf(_resultsTab);
        _mode = EditorMode.Command;
    }

    // Hook called from HandleKey before any other dispatch. Returns true
    // when the key was consumed by the *Results* tab.
    private bool TryHandleResultsTabKey(ConsoleKeyInfo key)
    {
        if (_resultsTab is null || !ReferenceEquals(Current, _resultsTab)) return false;
        if (key.Key != ConsoleKey.Enter) return false;

        var line = _buffer.GetLine(_buffer.Cursor.Line).TrimStart();
        // Match "<lineNo>:<col>  <text>" pattern (indented match line) and
        // pair with the most recent header line that's a real file path.
        if (line.Length == 0 || !char.IsDigit(line[0]))
        {
            // The cursor is on a header (path) line — open the file at 1:1.
            var path = _buffer.GetLine(_buffer.Cursor.Line).Trim();
            if (File.Exists(path)) { OpenPathDirect(path); }
            else _message = "no match under cursor";
            return true;
        }
        var colonA = line.IndexOf(':');
        var colonB = colonA >= 0 ? line.IndexOf(':', colonA + 1) : -1;
        if (colonA < 0 || colonB < 0) { _message = "could not parse match line"; return true; }
        if (!int.TryParse(line.AsSpan(0, colonA), out var lineNo)
            || !int.TryParse(line.AsSpan(colonA + 1, colonB - colonA - 1), out var col))
        {
            _message = "could not parse match line";
            return true;
        }
        // Walk backwards to find the header path line.
        string? headerPath = null;
        for (var i = _buffer.Cursor.Line - 1; i >= 0; i--)
        {
            var h = _buffer.GetLine(i);
            if (string.IsNullOrWhiteSpace(h) || h.StartsWith("  ")) continue;
            if (h.StartsWith("grep:") || h.StartsWith("files scanned") || h.StartsWith("(press")) continue;
            headerPath = h.Trim();
            break;
        }
        if (headerPath is null || !File.Exists(headerPath))
        {
            _message = "could not resolve match path";
            return true;
        }

        OpenPathDirect(headerPath);
        // OpenPathDirect already switched to the newly opened tab.
        var li = Math.Min(lineNo - 1, _buffer.LineCount - 1);
        var ci = Math.Max(0, Math.Min(col - 1, _buffer.GetLineLength(li)));
        _buffer.MoveCursor(new TextLocation(li, ci));
        _message = $"jumped to {headerPath}:{lineNo}:{col}";
        return true;
    }

    // ─── ':gsub' — cross-file replace ────────────────────────────────────

    private void GsubCommand(string arg)
    {
        var parsed = ParseSubstitution(arg);
        if (parsed is null) { _message = "usage: :gsub/pat/repl/[flags]"; return; }
        var (pat, repl, flagStr) = parsed.Value;
        if (string.IsNullOrEmpty(pat)) { _message = "gsub: empty pattern"; return; }

        var flags = ReplaceFlags.Parse(flagStr);
        var rx = CompileSearchRegex(pat, flags, out var err);
        if (rx is null) { _message = $"bad pattern: {err}"; return; }

        var roots = ResolveSearchRoots();
        if (roots.Count == 0) { _message = "gsub: nowhere to search"; return; }

        var changedFiles = 0;
        var totalReplacements = 0;
        var scanned = 0;
        var skipped = 0;
        var report = new StringBuilder();
        report.Append("gsub: ").Append(pat).Append(" → ").Append(repl);
        if (!string.IsNullOrEmpty(flagStr)) report.Append(" [").Append(flagStr).Append(']');
        report.AppendLine().AppendLine();

        foreach (var root in roots)
            WalkAndGsub(root, rx, repl, flags, report,
                ref scanned, ref changedFiles, ref totalReplacements, ref skipped);

        report.AppendLine();
        report.Append("files scanned: ").Append(scanned)
              .Append("  changed: ").Append(changedFiles)
              .Append("  total replacements: ").Append(totalReplacements);
        if (skipped > 0) report.Append("  skipped (confirm=n): ").Append(skipped);
        report.AppendLine();

        ShowResults(report.ToString());
        _message = $"gsub: {totalReplacements} replacement(s) in {changedFiles} file(s)";
    }

    private void WalkAndGsub(
        string dir, Regex rx, string repl, ReplaceFlags flags, StringBuilder report,
        ref int scanned, ref int changedFiles, ref int totalReplacements, ref int skipped)
    {
        var workspaceExcludes = _workspace?.Exclude ?? Array.Empty<string>();
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name)) continue;
            if (AlwaysSkipDirs.Contains(name)) continue;
            if (workspaceExcludes.Contains(name, StringComparer.Ordinal)) continue;

            if (Directory.Exists(entry))
            {
                WalkAndGsub(entry, rx, repl, flags, report,
                    ref scanned, ref changedFiles, ref totalReplacements, ref skipped);
                continue;
            }

            if (!IsLikelyTextFile(entry)) continue;
            scanned++;

            string original;
            try { original = File.ReadAllText(entry); }
            catch { continue; }

            int fileCount;
            string updated;
            if (flags.All)
            {
                fileCount = 0;
                updated = rx.Replace(original, _ => { fileCount++; return repl; });
            }
            else
            {
                fileCount = 0;
                var lines = original.Replace("\r\n", "\n").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var m = rx.Match(lines[i]);
                    if (!m.Success) continue;
                    lines[i] = lines[i][..m.Index] + repl + lines[i][(m.Index + m.Length)..];
                    fileCount++;
                }
                updated = string.Join('\n', lines);
            }
            if (fileCount == 0) continue;

            if (flags.Confirm)
            {
                Render();
                DrawPrompt($"gsub {entry}: ", $"{fileCount} match(es) — apply? [y/n/a/q] ");
                var ans = _terminal.ReadKey();
                if (ans.KeyChar is 'q' or 'Q' || ans.Key == ConsoleKey.Escape)
                {
                    report.Append("[stopped at ").Append(entry).Append(']').AppendLine();
                    return;
                }
                if (ans.KeyChar is 'a' or 'A') flags = flags with { Confirm = false };
                else if (ans.KeyChar is not ('y' or 'Y'))
                {
                    skipped++;
                    report.Append("  skip   ").Append(entry).Append("  (").Append(fileCount).Append(")").AppendLine();
                    continue;
                }
            }

            try
            {
                File.WriteAllText(entry, updated);
                changedFiles++;
                totalReplacements += fileCount;
                report.Append("  ").Append(fileCount.ToString().PadLeft(4))
                      .Append("  ").Append(entry).AppendLine();
            }
            catch (Exception ex)
            {
                report.Append("  ERR   ").Append(entry).Append("  (").Append(ex.Message).Append(')').AppendLine();
            }
        }
    }
}

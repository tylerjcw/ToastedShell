using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Reload-on-disk-change. Polls the active tab's file mtime + size each
/// frame (rate-limited). Clean buffers reload silently; dirty buffers
/// surface a one-shot prompt so unsaved work isn't clobbered.
/// </summary>
internal sealed partial class TomeApp
{
    private DateTime _lastDiskPoll = DateTime.MinValue;
    private static readonly TimeSpan DiskPollInterval = TimeSpan.FromMilliseconds(500);

    private void CheckExternalChange()
    {
        if (Environment.GetEnvironmentVariable("TOME_NO_WATCH") == "1") return;
        var now = DateTime.UtcNow;
        if (now - _lastDiskPoll < DiskPollInterval) return;
        _lastDiskPoll = now;

        var tab = Current;
        var path = tab.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        if (tab.DiskSize < 0) return; // file didn't exist at load time

        DateTime mtime;
        long size;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            mtime = fi.LastWriteTimeUtc;
            size = fi.Length;
        }
        catch { return; }

        if (mtime == tab.DiskMTimeUtc && size == tab.DiskSize)
        {
            tab.ExternalChangePending = false;
            return;
        }

        if (tab.Buffer.IsModified)
        {
            // Don't clobber unsaved work. Surface a prompt; user picks
            // :reload to drop changes or :w! / :w to overwrite.
            if (!tab.ExternalChangePending)
            {
                tab.ExternalChangePending = true;
                _message = "file changed on disk; buffer is dirty — :reload to discard, :w to overwrite";
            }
            // Update stamps so we don't re-warn every poll until disk changes again.
            tab.DiskMTimeUtc = mtime;
            tab.DiskSize = size;
            return;
        }

        ReloadFromDisk(silent: false);
    }

    private void ReloadFromDisk(bool silent)
    {
        var tab = Current;
        var path = tab.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            _message = "reload: no file";
            return;
        }
        string text;
        try { text = File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch (Exception ex) { _message = $"reload failed: {ex.Message}"; return; }

        var savedCursor = tab.Buffer.Cursor;
        tab.Buffer.ReplaceAll(text);
        tab.Buffer.MarkClean();
        // Clamp the cursor to the new bounds.
        var line = Math.Max(0, Math.Min(savedCursor.Line, tab.Buffer.LineCount - 1));
        var lineLen = tab.Buffer.GetLineLength(line);
        var col = Math.Max(0, Math.Min(savedCursor.Column, lineLen));
        tab.Buffer.MoveCursor(new TextLocation(line, col));
        tab.View.EnsureCursorVisible();
        tab.StampFromDisk();
        if (!silent) _message = $"reloaded from disk: {Path.GetFileName(path)}";
    }
}

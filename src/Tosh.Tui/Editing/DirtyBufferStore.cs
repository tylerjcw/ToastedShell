using System.Security.Cryptography;
using System.Text;

namespace Tosh.Tui.Editing;

/// <summary>
/// Persists the unsaved content of a <see cref="TextBuffer"/> between
/// editor sessions, keyed by the absolute path of the file being edited.
/// Mirrors the storage layout of <see cref="PersistentUndoStore"/> but
/// stores only the raw dirty text so closing a modified tab never prompts
/// and the edits are silently recovered next time the file is opened.
///
/// File format ("TOMEDIR1"):
///   magic       : 8 bytes  ("TOMEDIR1")
///   diskHash    : 32 bytes (SHA256 of on-disk text at the time the dirty
///                           buffer was persisted — allows detecting
///                           external edits that would make the stash stale)
///   dirtyText   : 7-bit length-prefixed UTF-8 (BinaryWriter.Write(string))
///   cursorLine  : int32
///   cursorCol   : int32
/// </summary>
public static class DirtyBufferStore
{
    private const string Magic = "TOMEDIR1";
    private const int HashLen = 32;

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("TOME_NO_DIRTY_BUFFER") != "1";

    /// <summary>
    /// Persist the current (unsaved) content of <paramref name="buffer"/>
    /// for <paramref name="filePath"/>. <paramref name="diskText"/> is the
    /// last known on-disk content, used to detect stale stashes on restore.
    /// </summary>
    public static void Save(string filePath, TextBuffer buffer, string diskText)
    {
        if (!Enabled) return;
        if (string.IsNullOrEmpty(filePath)) return;
        if (!buffer.IsModified) { TryDelete(filePath); return; }

        try
        {
            var sidecar = SidecarPath(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);

            using var fs = File.Create(sidecar);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
            bw.Write(Encoding.ASCII.GetBytes(Magic));
            bw.Write(Sha256(diskText));
            bw.Write(buffer.GetText());
            bw.Write(buffer.Cursor.Line);
            bw.Write(buffer.Cursor.Column);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Try to restore dirty content for <paramref name="filePath"/> into
    /// <paramref name="buffer"/>. Only restores when the current on-disk
    /// text matches the hash stored at persist time (prevents overwriting a
    /// file that was edited externally).
    /// Returns true and marks the buffer as modified on success.
    /// </summary>
    public static bool TryRestore(string filePath, TextBuffer buffer)
    {
        if (!Enabled) return false;
        if (string.IsNullOrEmpty(filePath)) return false;
        var sidecar = SidecarPath(filePath);
        if (!File.Exists(sidecar)) return false;

        try
        {
            using var fs = File.OpenRead(sidecar);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

            var magic = Encoding.ASCII.GetString(br.ReadBytes(Magic.Length));
            if (magic != Magic) return false;

            var storedDiskHash = br.ReadBytes(HashLen);
            if (storedDiskHash.Length != HashLen) return false;

            var currentDiskHash = Sha256(buffer.GetText());
            if (!storedDiskHash.AsSpan().SequenceEqual(currentDiskHash))
                return false;

            var dirtyText = br.ReadString();
            var cursorLine = br.ReadInt32();
            var cursorCol = br.ReadInt32();

            buffer.ReplaceAll(dirtyText);
            buffer.MoveCursor(new TextLocation(cursorLine, cursorCol));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Delete any stored dirty stash for <paramref name="filePath"/>.</summary>
    public static void TryDelete(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try { File.Delete(SidecarPath(filePath)); }
        catch { }
    }

    public static string SidecarPath(string filePath)
    {
        var key = KeyFor(filePath);
        return Path.Combine(StateDir(), key + ".dirty");
    }

    private static string StateDir()
    {
        var custom = Environment.GetEnvironmentVariable("TOME_STATE_DIR");
        if (!string.IsNullOrEmpty(custom))
            return Path.Combine(custom, "dirty");

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(xdg))
            return Path.Combine(xdg, "tome", "dirty");

        var home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        return Path.Combine(home, ".local", "state", "tome", "dirty");
    }

    private static string KeyFor(string filePath)
    {
        var abs = Path.GetFullPath(filePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(abs));
        var sb = new StringBuilder(32);
        for (var i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    private static byte[] Sha256(string text) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
}

using System.Security.Cryptography;
using System.Text;

namespace Tosh.Tui.Editing;

/// <summary>
/// Side-car undo persistence. Writes the undo/redo stacks of a
/// <see cref="TextBuffer"/> to a binary file keyed by the absolute path of
/// the document being edited, and restores them on reopen if the on-disk
/// content matches the snapshot taken when the side-car was last written.
///
/// File format ("TOMEUND1"):
///   magic     : 8 bytes  ("TOMEUND1")
///   contentHash : 32 bytes (SHA256 of TextBuffer.GetText() at save time)
///   headLine   : int32
///   headCol    : int32
///   headDirty  : byte (always 0 in practice — we save right after MarkClean)
///   undoCount  : int32
///   undoFrames : undoCount frames, bottom → top
///   redoCount  : int32
///   redoFrames : redoCount frames, bottom → top
///
/// Frame:
///   cursorLine : int32
///   cursorCol  : int32
///   isModified : byte
///   lineCount  : int32
///   lines      : lineCount × (7-bit length + UTF-8 bytes)
/// </summary>
public static class PersistentUndoStore
{
    private const string Magic = "TOMEUND1";
    private const int HashLen = 32;
    private const int MaxFrames = 256;

    /// <summary>Set to false (or env <c>TOME_NO_PERSISTENT_UNDO=1</c>) to disable.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("TOME_NO_PERSISTENT_UNDO") != "1";

    /// <summary>
    /// Save the buffer's undo/redo history to the side-car for
    /// <paramref name="filePath"/>. Silently returns if disabled, the path
    /// is empty, or the buffer has no history.
    /// </summary>
    public static void Save(string filePath, TextBuffer buffer)
    {
        if (!Enabled) return;
        if (string.IsNullOrEmpty(filePath)) return;
        var undo = buffer.ExportUndoStack();
        var redo = buffer.ExportRedoStack();
        if (undo.Count == 0 && redo.Count == 0)
        {
            // Nothing to remember — also opportunistically delete a stale
            // side-car so we don't restore yesterday's history into a
            // fresh-edited file.
            TryDelete(filePath);
            return;
        }

        try
        {
            var sideCar = SideCarPath(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sideCar)!);

            var contentHash = Sha256(buffer.GetText());
            using var fs = File.Create(sideCar);
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
            bw.Write(Encoding.ASCII.GetBytes(Magic));
            bw.Write(contentHash);
            bw.Write(buffer.Cursor.Line);
            bw.Write(buffer.Cursor.Column);
            bw.Write((byte)(buffer.IsModified ? 1 : 0));

            // Cap stored frames so a giant history doesn't bloat the side-car;
            // keep the most recent ones (top of each stack).
            var undoSlice = TakeTail(undo, MaxFrames);
            var redoSlice = TakeTail(redo, MaxFrames);

            bw.Write(undoSlice.Count);
            foreach (var f in undoSlice) WriteFrame(bw, f);
            bw.Write(redoSlice.Count);
            foreach (var f in redoSlice) WriteFrame(bw, f);
        }
        catch
        {
            // Persistent undo is best-effort — never fail a save because of it.
        }
    }

    /// <summary>
    /// Attempt to restore undo/redo history for <paramref name="filePath"/>
    /// into <paramref name="buffer"/>. Only restores when the buffer's
    /// current text matches the side-car's stored content hash.
    /// Returns true if history was restored.
    /// </summary>
    public static bool TryRestore(string filePath, TextBuffer buffer)
    {
        if (!Enabled) return false;
        if (string.IsNullOrEmpty(filePath)) return false;
        var sideCar = SideCarPath(filePath);
        if (!File.Exists(sideCar)) return false;

        try
        {
            using var fs = File.OpenRead(sideCar);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

            var magic = Encoding.ASCII.GetString(br.ReadBytes(Magic.Length));
            if (magic != Magic) return false;

            var storedHash = br.ReadBytes(HashLen);
            if (storedHash.Length != HashLen) return false;
            var currentHash = Sha256(buffer.GetText());
            if (!storedHash.AsSpan().SequenceEqual(currentHash))
                return false;

            // headLine/headCol/headDirty — informational only; current buffer
            // already holds the head state. We still consume the bytes.
            _ = br.ReadInt32();
            _ = br.ReadInt32();
            _ = br.ReadByte();

            var undoCount = br.ReadInt32();
            if (undoCount < 0 || undoCount > MaxFrames * 2) return false;
            var undo = new UndoFrame[undoCount];
            for (var i = 0; i < undoCount; i++) undo[i] = ReadFrame(br);

            var redoCount = br.ReadInt32();
            if (redoCount < 0 || redoCount > MaxFrames * 2) return false;
            var redo = new UndoFrame[redoCount];
            for (var i = 0; i < redoCount; i++) redo[i] = ReadFrame(br);

            buffer.ImportHistory(undo, redo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Remove any stored side-car for the given path. Best-effort.</summary>
    public static void TryDelete(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try { File.Delete(SideCarPath(filePath)); }
        catch { /* ignore */ }
    }

    /// <summary>Resolve the on-disk path of the side-car for the given source file.</summary>
    public static string SideCarPath(string filePath)
    {
        var key = KeyFor(filePath);
        return Path.Combine(StateDir(), key + ".undo");
    }

    private static string StateDir()
    {
        var custom = Environment.GetEnvironmentVariable("TOME_STATE_DIR");
        if (!string.IsNullOrEmpty(custom))
            return Path.Combine(custom, "undo");

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(xdg))
            return Path.Combine(xdg, "tome", "undo");

        var home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        return Path.Combine(home, ".local", "state", "tome", "undo");
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

    private static void WriteFrame(BinaryWriter bw, UndoFrame f)
    {
        bw.Write(f.CursorLine);
        bw.Write(f.CursorColumn);
        bw.Write((byte)(f.IsModified ? 1 : 0));
        bw.Write(f.Lines.Length);
        foreach (var line in f.Lines) bw.Write(line ?? string.Empty);
    }

    private static UndoFrame ReadFrame(BinaryReader br)
    {
        var line = br.ReadInt32();
        var col = br.ReadInt32();
        var dirty = br.ReadByte() != 0;
        var lineCount = br.ReadInt32();
        if (lineCount < 0 || lineCount > 10_000_000)
            throw new InvalidDataException("frame line count out of range");
        var lines = new string[lineCount];
        for (var i = 0; i < lineCount; i++) lines[i] = br.ReadString();
        return new UndoFrame(lines, line, col, dirty);
    }

    private static IReadOnlyList<UndoFrame> TakeTail(IReadOnlyList<UndoFrame> all, int n)
    {
        if (all.Count <= n) return all;
        var result = new UndoFrame[n];
        for (var i = 0; i < n; i++) result[i] = all[all.Count - n + i];
        return result;
    }
}

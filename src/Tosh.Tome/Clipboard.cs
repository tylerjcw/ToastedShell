using System.Diagnostics;

namespace Tosh.Tome;

/// <summary>
/// Best-effort clipboard backed by Wayland (wl-copy/wl-paste) or X11 (xclip).
/// Falls back to an in-process string when no external tool is available, so
/// copy/cut/paste within a single Tōme session always works.
/// </summary>
internal static class Clipboard
{
    private static string _fallback = string.Empty;

    public static void SetText(string text)
    {
        _fallback = text ?? string.Empty;

        if (TryRunStdin("wl-copy", Array.Empty<string>(), _fallback)) return;
        if (TryRunStdin("xclip", new[] { "-selection", "clipboard" }, _fallback)) return;
        if (TryRunStdin("xsel", new[] { "--clipboard", "--input" }, _fallback)) return;
    }

    public static string GetText()
    {
        if (TryReadStdout("wl-paste", new[] { "--no-newline" }, out var wl)) return wl;
        if (TryReadStdout("xclip", new[] { "-selection", "clipboard", "-o" }, out var xc)) return xc;
        if (TryReadStdout("xsel", new[] { "--clipboard", "--output" }, out var xs)) return xs;
        return _fallback;
    }

    private static bool TryRunStdin(string command, IReadOnlyList<string> args, string input)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardInput.Write(input);
            proc.StandardInput.Close();
            proc.WaitForExit(500);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadStdout(string command, IReadOnlyList<string> args, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(500);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

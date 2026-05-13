using System.Diagnostics;
using Tosh.Crumb.Output;

namespace Tosh.Crumb.Aur;

/// <summary>
/// Pre-flight GPG key importer. AUR PKGBUILDs that use source
/// signatures list every required key id in <c>validpgpkeys</c>
/// inside <c>.SRCINFO</c>. If a key isn't in the local keyring,
/// <c>makepkg</c> fails late and cryptically with an integrity-check
/// error; this helper detects that ahead of time, prompts, and runs
/// <c>gpg --recv-keys</c>.
///
/// Paru offloads this to makepkg entirely, which works when
/// <c>~/.gnupg/gpg.conf</c> has <c>auto-key-retrieve</c> — not
/// everyone configures that, so crumb handles it explicitly.
/// </summary>
internal static class GpgKeys
{
    /// <summary>
    /// Parses <paramref name="srcInfoPath"/> for <c>validpgpkeys</c>
    /// entries, queries the local keyring for missing keys, prompts
    /// the user, and runs <c>gpg --recv-keys</c> for accepted keys.
    /// Returns true if the user accepted (or no keys were missing);
    /// false if they declined.
    /// </summary>
    public static async Task<bool> EnsureImportedAsync(string srcInfoPath, bool noConfirm, CancellationToken ct)
    {
        if (!File.Exists(srcInfoPath)) return true;
        var keys = await ParseKeysAsync(srcInfoPath, ct);
        if (keys.Count == 0) return true;

        var missing = new List<string>();
        foreach (var k in keys)
            if (!await HasKeyAsync(k, ct)) missing.Add(k);
        if (missing.Count == 0) return true;

        Confirm.Status(":: PGP keys needed:");
        foreach (var k in missing) Confirm.Status($"    {k}");
        if (!noConfirm && !Confirm.YesNo(":: Import missing key(s)?"))
            return false;

        var args = new List<string> { "--recv-keys" };
        args.AddRange(missing);
        var rc = await RunGpgAsync(args, ct);
        if (rc != 0)
        {
            Console.Error.WriteLine($"crumb: gpg --recv-keys failed (exit {rc})");
            return false;
        }
        return true;
    }

    private static async Task<List<string>> ParseKeysAsync(string srcInfoPath, CancellationToken ct)
    {
        var keys = new List<string>();
        foreach (var raw in await File.ReadAllLinesAsync(srcInfoPath, ct))
        {
            var line = raw.TrimStart();
            if (!line.StartsWith("validpgpkeys", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var v = line[(eq + 1)..].Trim();
            if (v.Length > 0) keys.Add(v);
        }
        return keys;
    }

    private static async Task<bool> HasKeyAsync(string keyId, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gpg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--list-keys");
        psi.ArgumentList.Add("--with-colons");
        psi.ArgumentList.Add(keyId);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<int> RunGpgAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gpg") { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var ttyScope = Tosh.Client.ChildTtyScope.Acquire();
            using var p = Process.Start(psi);
            if (p is null) return 127;
            await p.WaitForExitAsync(ct);
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 127;
        }
    }
}

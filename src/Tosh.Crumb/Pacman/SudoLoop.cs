using System.Diagnostics;

namespace Tosh.Crumb.Pacman;

/// <summary>
/// Keeps a <c>sudo</c> ticket fresh during a long-running operation
/// (an AUR build can easily out-last the default 15-minute sudo
/// timeout). Mirrors paru's <c>sudo_loop</c> in
/// <c>src/exec.rs</c>: prime with <c>sudo -v</c>, then refresh every
/// 250 s until the caller disposes us.
///
/// Only activates when the escalator resolves to <c>sudo</c>. For
/// <c>doas</c>, <c>pkexec</c>, or already-root we no-op — those
/// don't have a session ticket.
/// </summary>
internal sealed class SudoLoop : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task? _loop;
    private readonly string? _sudo;

    private SudoLoop(string? sudo, CancellationTokenSource cts, Task? loop)
    {
        _sudo = sudo;
        _cts = cts;
        _loop = loop;
    }

    public static async Task<SudoLoop> StartAsync(CancellationToken outer)
    {
        var resolved = Privilege.ResolveEscalator();
        var sudo = resolved.Count > 0 ? Path.GetFileName(resolved[0]) : null;
        if (sudo is null || !string.Equals(sudo, "sudo", StringComparison.Ordinal))
            return new SudoLoop(null, new CancellationTokenSource(), null);

        // Prime once up-front so the user gets prompted now, not in
        // the middle of a build.
        await RefreshAsync(resolved[0], outer);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(250), cts.Token);
                    await RefreshAsync(resolved[0], cts.Token);
                }
            }
            catch (OperationCanceledException) { /* expected on dispose */ }
        }, cts.Token);

        return new SudoLoop(sudo, cts, loop);
    }

    private static async Task RefreshAsync(string sudoPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(sudoPath) { UseShellExecute = false };
        psi.ArgumentList.Add("-v");
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return;
            await p.WaitForExitAsync(ct);
        }
        catch { /* best effort — next iteration will retry */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is null) return;
        try
        {
            _cts.Cancel();
            await _loop;
        }
        catch { /* ignore */ }
        finally { _cts.Dispose(); }
    }
}

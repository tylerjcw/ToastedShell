using System.Reflection;
using System.Reflection.Emit;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `load-assembly` loads, whatever order interdependent assemblies arrive in.
///
/// `TS-P2-96`. The command called `assembly.GetTypes().Length` purely to report a
/// type count, and <c>GetTypes</c> resolves the entire type closure — so it threw
/// whenever a referenced assembly had not been loaded *yet*. Loading Avalonia's
/// assemblies alphabetically failed on `Avalonia.Controls` because
/// `Avalonia.Remote.Protocol` had not been reached, though it sat in the same
/// directory.
///
/// The assembly is already in the load context when the throw happens, so the
/// load had in fact succeeded and only the reporting failed — the count was
/// taking the command down with it.
/// </summary>
public class LoadAssemblyClosureTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    // No in-process reproduction of the reported failure, deliberately, after two
    // attempts that proved nothing.
    //
    // The failure needs an assembly whose reference cannot be resolved *at the
    // moment it loads*. Emitting such a pair requires a real `Type` for the
    // referent, which means loading the referent first — and once it is in the
    // default load context it stays resolvable for the rest of the process,
    // whatever happens to the file on disk. Deleting the base assembly and even
    // deriving from it both passed with the fix reverted; the control is what said
    // so, and without it two vacuous tests would have shipped looking thorough.
    //
    // Reproducing it honestly needs a child process, which is a large amount of
    // machinery for one command. The fix is verified instead against a real
    // interdependent set — the 11 Avalonia reference assemblies loaded
    // alphabetically, which fail with "Unable to load one or more" before the
    // change and all load after it — and the controls below cover what can be
    // asserted in process: the count is still produced for a healthy assembly, and
    // a missing file is still an error.

    /// <summary>
    /// The control: a healthy assembly still reports its real type count, so the
    /// fix did not simply stop counting.
    /// </summary>
    [Fact]
    public async Task A_healthy_assembly_reports_its_types()
    {
        var path = typeof(ToshEngine).Assembly.Location;
        var count = await RunAsync($"(load-assembly \"{path}\").Types");

        Assert.True(int.TryParse(count, out var parsed), $"expected a number, got '{count}'");
        Assert.True(parsed > 100, $"Tosh.Language should define many types; got {parsed}.");
    }

    /// <summary>A missing file is still an error rather than a silent zero.</summary>
    [Fact]
    public async Task A_missing_assembly_is_still_reported()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault())
                .ExecuteToListAsync("load-assembly \"/tmp/tosh-no-such-assembly.dll\""));

        Assert.Contains("does not exist", exception.Message);
    }
}

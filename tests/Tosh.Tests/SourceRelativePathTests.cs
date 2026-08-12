using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>source</c> resolves a relative path against the sourcing script's directory —
/// <c>TS-P2-29</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>source "./x.tosh"</c> resolved against the working directory, so a script that sourced a
/// sibling ran only from its own directory: from anywhere else it looked for the sibling beside
/// the *caller*. Found while testing partial-module assembly, where a script in a temp directory
/// went looking in the repository root. <c>require</c> has always resolved against the requiring
/// script, so the two spellings disagreed about what a relative path means.
/// </para>
/// <para>
/// Unlike <c>require</c>, which resolves against the script directory and stops, a path that is
/// not there falls back to the old working-directory resolution — nothing shipped relied on that,
/// but it is the compatible direction and keeps the "not found" message coming from where it
/// always did.
/// </para>
/// </remarks>
public sealed class SourceRelativePathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tosh-p2-29-{Guid.NewGuid():N}");

    public SourceRelativePathTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        File.WriteAllText(Path.Combine(_root, "sub", "helper.tosh"), "var HELPER = 41\n");
        File.WriteAllText(Path.Combine(_root, "sub", "main.tosh"), "source \"./helper.tosh\"\n($HELPER + 1)\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Slash(string path) => path.Replace("\\", "/");

    /// <summary>Runs a script with <paramref name="workingDirectory"/> as the shell's cwd.</summary>
    private static async Task<object?> RunAsync(string scriptPath, string workingDirectory)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = workingDirectory;
        var engine = new ToshEngine(runtime);

        var results = await AsyncEnumerableExtensions.ToListAsync(
            engine.ExecuteScriptFileAsync(scriptPath, Array.Empty<object?>()), default);

        return results.LastOrDefault();
    }

    [Fact]
    public async Task A_sibling_resolves_from_the_scripts_own_directory()
    {
        var main = Path.Combine(_root, "sub", "main.tosh");

        Assert.Equal(42, Convert.ToInt32(await RunAsync(main, Path.Combine(_root, "sub"))));
    }

    [Fact]
    public async Task A_sibling_resolves_from_any_other_working_directory()
    {
        // The defect: this looked for `helper.tosh` beside the caller and reported it missing.
        var main = Path.Combine(_root, "sub", "main.tosh");

        Assert.Equal(42, Convert.ToInt32(await RunAsync(main, _root)));
        Assert.Equal(42, Convert.ToInt32(await RunAsync(main, Path.Combine(_root, "elsewhere"))));
    }

    [Fact]
    public async Task A_sourced_script_resolves_against_its_own_directory_in_turn()
    {
        // The directory that matters is the one holding the file being executed, not the one that
        // started the chain — so a sourced script reaches its *own* siblings.
        var mid = Path.Combine(_root, "sub", "mid.tosh");
        await File.WriteAllTextAsync(mid, "source \"./helper.tosh\"\n($HELPER + 100)\n");

        var outer = Path.Combine(_root, "outer.tosh");
        await File.WriteAllTextAsync(outer, $"source \"{Slash(mid)}\"\n");

        Assert.Equal(141, Convert.ToInt32(await RunAsync(outer, Path.Combine(_root, "elsewhere"))));
    }

    // ── what must keep working ─────────────────────────────────────────────────

    [Fact]
    public async Task A_path_relative_to_the_working_directory_still_resolves()
    {
        // Present only beside the caller, not beside the script. `require` would stop here;
        // `source` falls back, which is what makes this change compatible.
        await File.WriteAllTextAsync(
            Path.Combine(_root, "elsewhere", "only-here.tosh"), "var CWDVAL = 7\n");

        var user = Path.Combine(_root, "sub", "uses-cwd.tosh");
        await File.WriteAllTextAsync(user, "source \"./only-here.tosh\"\n($CWDVAL * 2)\n");

        Assert.Equal(14, Convert.ToInt32(await RunAsync(user, Path.Combine(_root, "elsewhere"))));
    }

    [Fact]
    public async Task An_absolute_path_is_unaffected()
    {
        var helper = Path.Combine(_root, "sub", "helper.tosh");
        var script = Path.Combine(_root, "abs.tosh");
        await File.WriteAllTextAsync(script, $"source \"{Slash(helper)}\"\n$HELPER\n");

        Assert.Equal(41, Convert.ToInt32(await RunAsync(script, Path.Combine(_root, "elsewhere"))));
    }

    [Fact]
    public async Task A_missing_file_still_reports_against_the_working_directory()
    {
        // Falling back means the message keeps naming the path the reader would expect, rather
        // than a script directory they never mentioned.
        var script = Path.Combine(_root, "sub", "absent.tosh");
        await File.WriteAllTextAsync(script, "source \"./definitely-absent.tosh\"\n");

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await RunAsync(script, Path.Combine(_root, "elsewhere")));

        Assert.Contains("definitely-absent.tosh", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_root, "elsewhere"), exception.Message, StringComparison.Ordinal);
    }
}

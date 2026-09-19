using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A function brought in by <c>require</c>, called from inside an interpolation hole.
/// </summary>
/// <remarks>
/// <para>
/// A <c>func</c> is declared into a lexical scope, not into the command registry, so the
/// binder — which is handed the registry — cannot see one. A source containing a
/// <c>require</c> therefore has its unknown-command check suppressed wholesale.
/// </para>
/// <para>
/// A hole is parsed at its first evaluation and bound as a source of its own, which has no
/// <c>require</c> in it, so the suppression never applied: <c>$"{ Whence() }"</c> reported a
/// function that <c>which</c> could find and that ran correctly outside the hole. But by
/// that point the requires above it have run, so the engine simply knows the answer — which
/// is better than suppressing the check, because the typo detection keeps working.
/// </para>
/// </remarks>
public sealed class InterpolationHoleBinderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tosh-hole-binder-");

    public void Dispose() => _root.Delete(recursive: true);

    private async Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);

        runtime.Config.Startup.LibraryDirectory = Path.Combine(_root.FullName, "lib");

        Directory.CreateDirectory(runtime.Config.Startup.LibraryDirectory);
        File.WriteAllText(
            Path.Combine(runtime.Config.Startup.LibraryDirectory, "Helper.tosh"),
            // Named to collide with the builtin `whence`, because the binder only complains
            // about names that look like typos for something it knows.
            """export func Whence() { return "from the library" }""");

        var scriptPath = Path.Combine(_root.FullName, "main.tosh");

        File.WriteAllText(scriptPath, source);

        var engine = new ToshEngine(runtime.Language);
        var results = new List<object?>();

        await foreach (var value in engine.EvaluateAsync(source, scriptPath))
        {
            results.Add(value);
        }

        return results;
    }

    /// <summary>The case that failed: a required function inside a hole.</summary>
    [Fact]
    public async Task A_required_function_can_be_called_from_an_interpolation_hole()
    {
        var results = await RunAsync("""
            require Helper
            echo $"said: { Whence() }"
            """);

        Assert.Equal("said: from the library", results.OfType<string>().Last());
    }

    /// <summary>It worked outside a hole all along, and still does.</summary>
    [Fact]
    public async Task The_same_call_outside_a_hole_still_works()
    {
        var results = await RunAsync("""
            require Helper
            echo (Whence())
            """);

        Assert.Equal("from the library", results.OfType<string>().Last());
    }

    /// <summary>
    /// A genuine typo inside a hole is still reported.
    /// </summary>
    /// <remarks>
    /// The point of telling the binder what exists rather than suppressing the check: a
    /// source that requires something does not stop being checked for typos. `ech` is one
    /// edit from `echo`, which is what the binder is looking for.
    /// </remarks>
    [Fact]
    public async Task A_typo_inside_a_hole_is_still_reported()
    {
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("""
                require Helper
                echo $"said: { ech() }"
                """));

        Assert.Contains("ech", error.Message, StringComparison.Ordinal);
    }
}

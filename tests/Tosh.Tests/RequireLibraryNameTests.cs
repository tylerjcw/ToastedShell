using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Requiring from the reader's library by name rather than by path.
/// </summary>
/// <remarks>
/// <para>
/// A library name means the same thing wherever it is required from, so the library is
/// searched before the directory the script happens to live in. A stray <c>Shell.tosh</c>
/// beside a script cannot quietly take the place of the library's.
/// </para>
/// <para>
/// An extension is how you say you mean a file: <c>require foo.tosh</c> is the file, never
/// the name <c>foo/tosh</c>. So is anything written with a separator or a <c>./</c>,
/// <c>~</c> or rooted prefix.
/// </para>
/// </remarks>
public sealed class RequireLibraryNameTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tosh-library-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Library => Path.Combine(_root.FullName, "lib");

    private string Scripts => Path.Combine(_root.FullName, "scripts");

    private void WriteLibrary(string relativePath, string contents) =>
        Write(Path.Combine(Library, relativePath), contents);

    private void WriteScript(string relativePath, string contents) =>
        Write(Path.Combine(Scripts, relativePath), contents);

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private async Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);

        runtime.Config.Startup.LibraryDirectory = Library;

        var scriptPath = Path.Combine(Scripts, "main.tosh");

        Write(scriptPath, source);

        var engine = new ToshEngine(runtime.Language);
        var results = new List<object?>();

        await foreach (var value in engine.EvaluateAsync(source, scriptPath))
        {
            results.Add(value);
        }

        return results;
    }

    private async Task<string> TextOf(string source) =>
        (await RunAsync(source)).Select(r => r?.ToString()).Last(r => !string.IsNullOrEmpty(r))!;

    /// <summary>A dotted name is a path through the library.</summary>
    [Fact]
    public async Task A_dotted_name_resolves_under_the_library()
    {
        WriteLibrary(Path.Combine("Core", "Shell.tosh"), """export func Speak() { return "core" }""");

        Assert.Equal("core", await TextOf("require Core.Shell\necho (Speak())"));
    }

    /// <summary>
    /// A directory with an <c>init.tosh</c> is a module too.
    /// </summary>
    /// <remarks>
    /// So a module can grow from one file into a folder without every caller changing.
    /// </remarks>
    [Fact]
    public async Task A_directory_with_an_init_file_is_a_module()
    {
        WriteLibrary(Path.Combine("Pkg", "init.tosh"), """export func Speak() { return "package" }""");

        Assert.Equal("package", await TextOf("require Pkg\necho (Speak())"));
    }

    /// <summary>
    /// The library is searched before the script's own directory.
    /// </summary>
    /// <remarks>
    /// The point of the order: a name means the same thing wherever it is required from.
    /// </remarks>
    [Fact]
    public async Task The_library_is_searched_before_the_script_directory()
    {
        WriteLibrary("Shell.tosh", """export func Speak() { return "library" }""");
        WriteScript("Shell.tosh", """export func Speak() { return "local" }""");

        Assert.Equal("library", await TextOf("require Shell\necho (Speak())"));
    }

    /// <summary>An extension says you mean a file, and gets you the local one.</summary>
    [Theory]
    [InlineData("Shell.tosh")]
    [InlineData("./Shell.tosh")]
    public async Task An_extension_or_a_path_means_the_file(string target)
    {
        WriteLibrary("Shell.tosh", """export func Speak() { return "library" }""");
        WriteScript("Shell.tosh", """export func Speak() { return "local" }""");

        Assert.Equal("local", await TextOf($"require {target}\necho (Speak())"));
    }

    /// <summary>
    /// A name that is both a file and a package is reported, not silently decided.
    /// </summary>
    /// <remarks>
    /// It is what a half-finished move from the one to the other looks like, and picking
    /// either would hide it.
    /// </remarks>
    [Fact]
    public async Task A_name_that_is_both_a_file_and_a_package_is_refused()
    {
        WriteLibrary(Path.Combine("Core", "Shell.tosh"), """export func Speak() { return "file" }""");
        WriteLibrary(Path.Combine("Core", "Shell", "init.tosh"), """export func Speak() { return "package" }""");

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("require Core.Shell"));

        Assert.Contains("both a file and a package", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name cannot climb out of the library.
    /// </summary>
    /// <remarks>
    /// <c>..</c> in a require is either a mistake or an attempt to reach somewhere the
    /// reader did not mean to expose. The path form is there for naming a file elsewhere on
    /// purpose.
    /// </remarks>
    [Fact]
    public async Task A_name_cannot_escape_the_library()
    {
        WriteLibrary("Outside.tosh", """export func Speak() { return "library" }""");
        Write(Path.Combine(_root.FullName, "Outside.tosh"), """export func Speak() { return "escaped" }""");

        // `..Outside` has an empty segment, so it is not a library name at all and falls
        // through to the path form, which does not find it either.
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync("require ..Outside\necho (Speak())"));
    }

    /// <summary>The failure names the library, not just a path nobody wrote.</summary>
    /// <remarks>
    /// A name meant for the library resolves to a path beside the script, so a message
    /// naming only that path points somewhere the author never mentioned.
    /// </remarks>
    [Fact]
    public async Task The_failure_says_the_library_was_searched()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync("require Core.Missing"));

        Assert.Contains("library", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Library, error.Message, StringComparison.Ordinal);
    }
}

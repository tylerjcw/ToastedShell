using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Reaching a library function by its qualified name, without requiring it first.
/// </summary>
/// <remarks>
/// <para>
/// <c>ToastLib.Math.Clamp(…)</c> says which module it wants, so loading it is a lookup
/// rather than a guess. That is what makes this safe where autoloading a bare name is not:
/// in a real library thirteen exported names appear in more than one file — <c>Window</c> in
/// three graphics backends — and none of them collide once the module is named.
/// </para>
/// <para>
/// A module name is not a file path. A partial module is spread across as many files as it
/// likes, so the answer comes from an index of what each file exports rather than from
/// probing directories.
/// </para>
/// </remarks>
public sealed class QualifiedAutoloadTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tosh-autoload-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Library => Path.Combine(_root.FullName, "lib");

    private void Write(string relative, string contents)
    {
        var path = Path.Combine(Library, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private async Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);

        runtime.Config.Startup.LibraryDirectory = Library;

        var engine = new ToshEngine(runtime.Language);
        var results = new List<object?>();

        await foreach (var value in engine.EvaluateAsync(source, Path.Combine(_root.FullName, "main.tosh")))
        {
            results.Add(value);
        }

        return results;
    }

    /// <summary>The whole point: no require, and it works.</summary>
    [Fact]
    public async Task A_qualified_name_loads_its_module()
    {
        Write("Math/Scalar.tosh", """
            export partial module Demo.Math

            export func Twice(n) { return $n * 2 }
            """);

        var results = await RunAsync("echo (Demo.Math.Twice(21))");

        Assert.Equal("42", results.Select(r => r?.ToString()).Last());
    }

    /// <summary>
    /// A module spread over several files loads only the file that has the member.
    /// </summary>
    /// <remarks>
    /// Which is why the index is keyed by member and not by module: requiring the whole
    /// module would load files the caller never asked about, and that is the cost this
    /// avoids.
    /// </remarks>
    [Fact]
    public async Task A_partial_module_loads_the_file_holding_the_member()
    {
        Write("Math/Scalar.tosh", """
            export partial module Demo.Math

            export func Twice(n) { return $n * 2 }
            """);
        Write("Math/Vector.tosh", """
            export partial module Demo.Math

            export func Thrice(n) { return $n * 3 }
            """);

        var results = await RunAsync("echo (Demo.Math.Thrice(14))");

        Assert.Equal("42", results.Select(r => r?.ToString()).Last());
    }

    /// <summary>
    /// The same name in two modules is not a collision, because the module is named.
    /// </summary>
    /// <remarks>
    /// The reason qualified access can autoload at all. Bare-name autoload would have to
    /// choose between these two, and there is no honest way to.
    /// </remarks>
    [Fact]
    public async Task The_same_name_in_two_modules_is_unambiguous()
    {
        Write("A.tosh", """
            export partial module Demo.Left

            export func Window() { return "left" }
            """);
        Write("B.tosh", """
            export partial module Demo.Right

            export func Window() { return "right" }
            """);

        Assert.Equal("left", (await RunAsync("echo (Demo.Left.Window())")).Select(r => r?.ToString()).Last());
        Assert.Equal("right", (await RunAsync("echo (Demo.Right.Window())")).Select(r => r?.ToString()).Last());
    }

    /// <summary>Two files exporting one name into one module is reported, not picked.</summary>
    [Fact]
    public async Task An_ambiguous_export_within_a_module_is_refused()
    {
        Write("One.tosh", """
            export partial module Demo.Math

            export func Twice(n) { return $n * 2 }
            """);
        Write("Two.tosh", """
            export partial module Demo.Math

            export func Twice(n) { return $n * 20 }
            """);

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("echo (Demo.Math.Twice(21))"));

        Assert.Contains("more than one file", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name nothing exports still fails, and says so.
    /// </summary>
    /// <remarks>
    /// Autoload is asked only once every ordinary path has declined, so it can neither
    /// shadow something that already resolves nor turn a mistake into a silence.
    /// </remarks>
    [Fact]
    public async Task An_unknown_qualified_name_still_fails()
    {
        Write("Math/Scalar.tosh", """
            export partial module Demo.Math

            export func Twice(n) { return $n * 2 }
            """);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync("echo (Demo.Math.Nonesuch(1))"));

        Assert.Contains("Demo.Math.Nonesuch", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A member read rather than called loads its module too.
    /// </summary>
    /// <remarks>
    /// This one failed quietly, which is worse than failing: an unresolved dotted name falls
    /// back to the path <em>as a string</em>, so <c>Demo.Consts.Answer</c> printed itself and
    /// looked like an answer.
    /// </remarks>
    [Fact]
    public async Task A_member_that_is_read_loads_its_module()
    {
        Write("Consts.tosh", """
            export partial module Demo.Consts

            export var Answer = 42
            """);

        Assert.Equal("42", (await RunAsync("echo (Demo.Consts.Answer)")).Select(r => r?.ToString()).Last());
    }

    /// <summary>And inside an interpolation hole, which is where a constant usually appears.</summary>
    [Fact]
    public async Task A_member_read_inside_an_interpolation_hole_loads_its_module()
    {
        Write("Consts.tosh", """
            export partial module Demo.Consts

            export var Answer = 42
            """);

        var results = await RunAsync("""echo $"answer: { Demo.Consts.Answer }" """);

        Assert.Equal("answer: 42", results.OfType<string>().Last());
    }

    /// <summary>
    /// A dotted name the library does not hold is still just a bareword.
    /// </summary>
    /// <remarks>
    /// The fallback is deliberate — a bareword is a string — so autoload must not turn an
    /// ordinary word into an error, only rescue one the library can answer.
    /// </remarks>
    [Fact]
    public async Task An_unknown_dotted_name_is_still_a_bareword()
    {
        Write("Consts.tosh", """
            export partial module Demo.Consts

            export var Answer = 42
            """);

        Assert.Equal(
            "Demo.Consts.Missing",
            (await RunAsync("echo (Demo.Consts.Missing)")).Select(r => r?.ToString()).Last());
    }

    /// <summary>A real CLR path is untouched by any of this.</summary>
    [Fact]
    public async Task Clr_paths_are_unaffected()
    {
        var results = await RunAsync("echo (System.Math.Abs(-5))");

        Assert.Equal("5", results.Select(r => r?.ToString()).Last());
    }
}

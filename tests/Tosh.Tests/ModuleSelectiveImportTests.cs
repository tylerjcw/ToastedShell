using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Taking named members from a module rather than from a file.
/// </summary>
/// <remarks>
/// <para>
/// A module is the unit of meaning and a file is where some of it happens to live. A partial
/// module spreads over as many files as it likes, so two members of one module can come from
/// two different files — and only the files actually asked for are loaded.
/// </para>
/// <para>
/// The module is preferred over a file of the same name, but only when it holds every name
/// the statement asks for. Where it cannot supply them all, nothing is loaded and the path
/// takes over unchanged.
/// </para>
/// </remarks>
public sealed class ModuleSelectiveImportTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tosh-module-import-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Library => Path.Combine(_root.FullName, "lib");

    private void Write(string relative, string contents)
    {
        var path = Path.Combine(Library, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private async Task<string> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);

        runtime.Config.Startup.LibraryDirectory = Library;

        var engine = new ToshEngine(runtime.Language);
        var results = new List<object?>();

        await foreach (var value in engine.EvaluateAsync(source, Path.Combine(_root.FullName, "main.tosh")))
        {
            results.Add(value);
        }

        return string.Join("|", results.Select(r => r?.ToString()));
    }

    private void WriteMathModule()
    {
        Write("Math/Scalar.tosh", """
            export partial module Demo.Math

            export func Clamp(x, lo, hi) { if ($x < $lo) { return $lo }; if ($x > $hi) { return $hi }; return $x }
            export func IntegerPart(x) => System.Math.Truncate($x)
            """);
        Write("Math/Vector.tosh", """
            export partial module Demo.Math

            export func Dot(a, b) { return ($a * $b) }
            """);
    }

    /// <summary>One member, named.</summary>
    [Fact]
    public async Task A_single_member_can_be_taken_from_a_module()
    {
        WriteMathModule();

        Assert.Equal("10", await RunAsync("""
            require Clamp from Demo.Math
            echo (Clamp(15, 0, 10))
            """));
    }

    /// <summary>Several at once.</summary>
    [Fact]
    public async Task Several_members_can_be_taken_at_once()
    {
        WriteMathModule();

        Assert.Equal("10|3", await RunAsync("""
            require { Clamp, IntegerPart } from Demo.Math
            echo (Clamp(15, 0, 10))
            echo (IntegerPart(3.7))
            """));
    }

    /// <summary>
    /// Each under a name of the caller's choosing.
    /// </summary>
    /// <remarks>
    /// The aliases here are lower-case on purpose. An alias whose first letter is capitalised
    /// binds, and answers in command position — `Pin 15 0 10` works — but is not found in
    /// expression position, where `Pin(15)` is read as a .NET access path. That is older than
    /// this feature and independent of it: an un-aliased `Clamp(15, 0, 10)` resolves fine, so
    /// it is the aliasing rather than the capital that is at fault.
    /// </remarks>
    [Fact]
    public async Task Members_can_be_renamed_as_they_are_taken()
    {
        WriteMathModule();

        Assert.Equal("10|3", await RunAsync("""
            require { Clamp as pin, IntegerPart as iPart } from Demo.Math
            echo (pin(15, 0, 10))
            echo (iPart(3.7))
            """));
    }

    /// <summary>A capitalised alias still binds, and answers where a command is expected.</summary>
    /// <remarks>
    /// Pinned so the gap above is recorded rather than merely avoided: the import works, and
    /// only one of the two call positions can see it.
    /// </remarks>
    [Fact]
    public async Task A_capitalised_alias_binds_even_where_expression_position_cannot_see_it()
    {
        WriteMathModule();

        Assert.Equal("10", await RunAsync("""
            require { Clamp as Pin } from Demo.Math
            Pin 15 0 10
            """));
    }

    /// <summary>
    /// Members of one module living in different files each load their own.
    /// </summary>
    /// <remarks>
    /// Which is the reason the index is keyed by member rather than by module: requiring the
    /// module whole would load files nobody asked about.
    /// </remarks>
    [Fact]
    public async Task Members_from_different_files_of_one_module_both_arrive()
    {
        WriteMathModule();

        Assert.Equal("10|12", await RunAsync("""
            require { Clamp, Dot } from Demo.Math
            echo (Clamp(15, 0, 10))
            echo (Dot(3, 4))
            """));
    }

    /// <summary>
    /// A module is preferred over a file of the same name.
    /// </summary>
    /// <remarks>
    /// `Demo.Math` can be both — a module, and an aggregating file at `Demo/Math.tosh`.
    /// Taking the module loads the one file holding the member rather than everything the
    /// aggregator pulls in.
    /// </remarks>
    [Fact]
    public async Task A_module_wins_over_a_file_of_the_same_name()
    {
        WriteMathModule();
        Write("Demo/Math.tosh", """
            # An aggregator that would drag in everything.
            export func Clamp(x, lo, hi) { return "from the aggregator" }
            """);

        Assert.Equal("10", await RunAsync("""
            require Clamp from Demo.Math
            echo (Clamp(15, 0, 10))
            """));
    }

    /// <summary>
    /// Where the module cannot supply every name, the path takes over unchanged.
    /// </summary>
    /// <remarks>
    /// Nothing is half-loaded: a require that worked as a path before still means what it
    /// did, which is what keeps this additive.
    /// </remarks>
    [Fact]
    public async Task A_name_the_module_lacks_falls_back_to_the_file()
    {
        Write("Flat.tosh", """
            export func Alpha() { return "flat" }
            """);

        Assert.Equal("flat", await RunAsync("""
            require { Alpha } from Flat
            echo (Alpha())
            """));
    }

    /// <summary>Two files exporting one name into one module is reported, not picked.</summary>
    [Fact]
    public async Task An_ambiguous_member_is_refused()
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
            () => RunAsync("require { Twice } from Demo.Math"));

        Assert.Contains("more than one file", error.Message, StringComparison.Ordinal);
    }
}

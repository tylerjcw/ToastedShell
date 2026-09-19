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
    /// Each under a name of the caller's choosing, wherever it is called.
    /// </summary>
    /// <remarks>
    /// An alias used to bind a wrapper that implemented only <c>IShellCommand</c>, so it
    /// answered where a command was expected — <c>Pin 15 0 10</c> — and not where a value
    /// was: <c>Pin(15)</c> reported "Unable to resolve .NET access path". The un-aliased
    /// import worked because nothing wrapped it. Both positions are asserted here because
    /// only one of them was broken.
    /// </remarks>
    [Fact]
    public async Task Members_can_be_renamed_as_they_are_taken()
    {
        WriteMathModule();

        Assert.Equal("10|3", await RunAsync("""
            require { Clamp as Pin, IntegerPart as iPart } from Demo.Math
            echo (Pin(15, 0, 10))
            echo (iPart(3.7))
            """));
    }

    /// <summary>An alias answers in command position as well.</summary>
    [Fact]
    public async Task An_alias_answers_in_command_position_too()
    {
        WriteMathModule();

        Assert.Equal("10", await RunAsync("""
            require { Clamp as Pin } from Demo.Math
            Pin 15 0 10
            """));
    }

    /// <summary>
    /// And can be handed to the platform, like the function it renames.
    /// </summary>
    /// <remarks>
    /// The wrapper forwards <c>ISelfHostedCallable</c>, which is what a CLR delegate
    /// conversion looks for. Without it an alias could be called from tōsh and not passed to
    /// .NET, which is a strange thing for a rename to change.
    /// </remarks>
    [Fact]
    public async Task An_alias_can_still_become_a_clr_delegate()
    {
        Write("Cmp.tosh", """
            export func ByLen(a, b) { return $a.Length - $b.Length }
            """);

        // `Sort` returns void, and a bare void call emits its receiver, so the List is in
        // the results ahead of the echoed line.
        var output = await RunAsync("""
            require { ByLen as Shorter } from Cmp
            var lst = new System.Collections.Generic.List<string>(["bbb", "a", "cc"])
            $lst.Sort(&Shorter)
            echo $"{$lst[0]},{$lst[1]},{$lst[2]}"
            """);

        Assert.EndsWith("a,cc,bbb", output, StringComparison.Ordinal);
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

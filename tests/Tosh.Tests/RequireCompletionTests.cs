using Tosh.Cli;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Completing a <c>require</c> from the reader's library.
/// </summary>
/// <remarks>
/// Names are offered only where the reader has not already written a path: `require ./`
/// goes on completing files as it always did, because at that point they have said what
/// they mean.
/// </remarks>
public sealed class RequireCompletionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tosh-require-completion-");
    private readonly ToshRuntime _runtime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);

    public RequireCompletionTests()
    {
        _runtime.Config.Startup.LibraryDirectory = Path.Combine(_root.FullName, "lib");

        Add("Core/Shell.tosh");
        Add("Core/System.tosh");
        Add("Pkg/init.tosh");
        Add("Pkg/Inner.tosh");
        Add("Top.tosh");
    }

    public void Dispose() => _root.Delete(recursive: true);

    private void Add(string relative)
    {
        var path = Path.Combine(_runtime.Config.Startup.LibraryDirectory, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "export func Placeholder() { return 1 }");
    }

    private IReadOnlyList<string> Complete(string line)
    {
        var engine = new ReplCompletionEngine(_runtime);
        var result = engine.GetCompletions(line, line.Length);

        return result is null ? [] : [.. result.Suggestions.Select(s => s.Label)];
    }

    /// <summary>Every module the library holds, by the name that requires it.</summary>
    [Fact]
    public void A_bare_prefix_offers_library_names()
    {
        var labels = Complete("require ");

        Assert.Contains("Core.Shell", labels);
        Assert.Contains("Core.System", labels);
        Assert.Contains("Top", labels);
    }

    /// <summary>
    /// A package is offered under its directory's name.
    /// </summary>
    /// <remarks>
    /// Because that is what requiring it gets you — <c>Pkg</c> loads <c>Pkg/init.tosh</c>.
    /// Offering <c>Pkg.init</c> would name a file nobody writes.
    /// </remarks>
    [Fact]
    public void A_package_is_offered_under_its_own_name()
    {
        var labels = Complete("require ");

        Assert.Contains("Pkg", labels);
        Assert.Contains("Pkg.Inner", labels);
        Assert.DoesNotContain("Pkg.init", labels);
    }

    /// <summary>A partial name narrows to that branch.</summary>
    [Fact]
    public void A_partial_name_narrows_the_offer()
    {
        var labels = Complete("require Core.");

        Assert.Contains("Core.Shell", labels);
        Assert.DoesNotContain("Top", labels);
    }

    /// <summary>
    /// Once it is written as a path, the library steps out of the way.
    /// </summary>
    /// <remarks>
    /// The same signals the resolver uses: a separator, a `./` or `~` prefix, an extension,
    /// or a quote. A reader who has typed one of those has said they mean a file.
    /// </remarks>
    [Theory]
    [InlineData("require ./")]
    [InlineData("require ~/")]
    [InlineData("require /usr/")]
    public void A_path_prefix_is_left_to_path_completion(string line)
        => Assert.DoesNotContain("Core.Shell", Complete(line));
}

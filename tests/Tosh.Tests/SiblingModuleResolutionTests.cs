using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A sibling module resolves the same whether the tree is one file or many —
/// <c>TOAST-0141</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>module Build { module Publish { … } module Packaging { … } }</c> resolves
/// <c>Packaging</c> from inside <c>Publish</c> because both are declared into one scope.
/// Split into <c>partial module Build.Publish</c> and <c>partial module Build.Packaging</c>,
/// each <c>require</c> gets its own scope, so a module required *later* was invisible and the
/// call failed at runtime with "Unable to resolve .NET access path" — splitting a module tree
/// into a file per module changed what a name meant.
/// </para>
/// <para>
/// The enclosing module's export table is shared across its partial declarations, so the
/// sibling was already reachable at call time; the lookup simply did not consult it.
/// </para>
/// </remarks>
public sealed class SiblingModuleResolutionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sibling-modules-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static async Task<string> RunAsync(string source, string path)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = new List<object?>();

        await foreach (var value in engine.EvaluateAsync(source, path))
        {
            results.Add(value);
        }

        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>The form the split was made from, and the behaviour it is measured against.</summary>
    [Fact]
    public async Task One_file_resolves_a_sibling_declared_later()
    {
        var main = Write("nested.tosh",
            """
            module Demo {
                module User { func Call() => Late.Hello() }
                module Late { func Hello() => "late" }
            }
            Demo.User.Call()
            """);

        Assert.Equal("late", await RunAsync(File.ReadAllText(main), main));
    }

    /// <summary>
    /// And the split form agrees, whichever order the files were required in.
    /// </summary>
    [Theory]
    [InlineData("Late", "User")]
    [InlineData("User", "Late")]
    public async Task Separate_files_resolve_a_sibling_in_either_order(string first, string second)
    {
        Write("User.tosh", "partial module Demo.User\nfunc Call() => Late.Hello()\n");
        Write("Late.tosh", "partial module Demo.Late\nfunc Hello() => \"late\"\n");
        var main = Write("main.tosh",
            $"require ./{first}.tosh\nrequire ./{second}.tosh\nDemo.User.Call()\n");

        Assert.Equal("late", await RunAsync(File.ReadAllText(main), main));
    }

    /// <summary>
    /// A cycle resolves, which no load order could have satisfied.
    /// </summary>
    /// <remarks>
    /// The tree this was found on has two — `WorkspaceLock` and `Diagnostics` each reach the
    /// other, as do `Publish`, `Packaging` and `Extension`. Requiring each module from the
    /// file that uses it does not help: `require` detects the cycle and refuses.
    /// </remarks>
    [Fact]
    public async Task Two_modules_that_reach_each_other_both_resolve()
    {
        Write("A.tosh", "partial module Demo.A\nfunc Name() => \"a\"\nfunc ViaB() => B.Name()\n");
        Write("B.tosh", "partial module Demo.B\nfunc Name() => \"b\"\nfunc ViaA() => A.Name()\n");
        var main = Write("main.tosh",
            "require ./A.tosh\nrequire ./B.tosh\n$\"{Demo.A.ViaB()}{Demo.B.ViaA()}\"\n");

        Assert.Equal("ba", await RunAsync(File.ReadAllText(main), main));
    }

    /// <summary>
    /// The fallback is a last resort: a nearer binding still wins.
    /// </summary>
    /// <remarks>
    /// It is consulted only after the scope walk and the runtime registry, so it can answer
    /// names that previously failed and cannot shadow a local, an import, or a nearer module.
    /// </remarks>
    [Fact]
    public async Task A_local_binding_is_not_shadowed_by_a_sibling()
    {
        Write("Shadow.tosh",
            """
            partial module Demo.Shadow
            func Call() {
                var Late = {| Hello: "local" |}
                return $Late.Hello
            }
            """);
        Write("Late.tosh", "partial module Demo.Late\nfunc Hello() => \"sibling\"\n");
        var main = Write("main.tosh", "require ./Shadow.tosh\nrequire ./Late.tosh\nDemo.Shadow.Call()\n");

        Assert.Equal("local", await RunAsync(File.ReadAllText(main), main));
    }

    /// <summary>A name that is nobody's sibling still fails, and says so.</summary>
    [Fact]
    public async Task An_unrelated_name_still_fails()
    {
        Write("User.tosh", "partial module Demo.User\nfunc Call() => Nowhere.Hello()\n");
        var main = Write("main.tosh", "require ./User.tosh\nDemo.User.Call()\n");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync(File.ReadAllText(main), main));

        Assert.Contains("Nowhere", error.Message, StringComparison.Ordinal);
    }
}

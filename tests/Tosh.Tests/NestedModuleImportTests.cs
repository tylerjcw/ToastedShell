using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Importing a nested module by dotted path — <c>require Outer.Inner from "…" as
/// Alias</c> (<c>TS-P2-35</c>).
/// </summary>
/// <remarks>
/// <para>
/// Reported from a library organised as nested modules to form a namespace-like
/// structure. Only the outermost name resolved, so the dotted form reported the whole
/// string as a missing export — accurate but unhelpful, since <c>Outer</c> was there
/// and <c>Inner</c> was inside it.
/// </para>
/// <para>
/// The fix had to land **twice**, and the first attempt looked correct while changing
/// nothing. <c>ToshEngine</c> carries two <c>ImportRequiredArtifact</c> overloads
/// twelve thousand lines apart — one taking name/alias arrays, one iterating
/// <c>statement.Imports</c> — and the <c>require</c> statement path uses the second.
/// Patching the first left the feature compiled and unreachable. This is
/// <c>TS-P1-24</c>'s failure mode on an axis that item does not cover: not a
/// sync/async twin, just two implementations of one operation.
/// </para>
/// </remarks>
public sealed class NestedModuleImportTests
{
    private sealed class ScriptSet : IDisposable
    {
        public ScriptSet()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tosh-nested-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string name, string contents) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), contents);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static async Task<IReadOnlyList<object?>> RunAsync(ScriptSet scripts, string main)
    {
        var path = System.IO.Path.Combine(scripts.Path, "main.tosh");
        File.WriteAllText(path, main);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = scripts.Path;
        var engine = new ToshEngine(runtime.Language);

        return await engine.ExecuteToListAsync(main, path);
    }

    private const string Library =
        """
        module Outer {
            module Inner {
                export func F() -> string { return "inner-f" }
            }
            module Deeper {
                module Deepest {
                    export func G() -> string { return "deepest-g" }
                }
            }
        }
        """;

    [Fact]
    public async Task A_nested_module_imports_by_dotted_path()
    {
        using var scripts = new ScriptSet();
        scripts.Write("lib.tosh", Library);

        var results = await RunAsync(
            scripts,
            "require Outer.Inner from \"./lib.tosh\" as Inn\nInn.F()");

        Assert.Equal("inner-f", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task The_path_may_be_more_than_two_deep()
    {
        using var scripts = new ScriptSet();
        scripts.Write("lib.tosh", Library);

        var results = await RunAsync(
            scripts,
            "require Outer.Deeper.Deepest from \"./lib.tosh\" as D\nD.G()");

        Assert.Equal("deepest-g", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task Without_an_alias_the_binding_takes_the_final_segment()
    {
        // A dotted path is not itself a usable identifier, so `Outer.Inner` binds as
        // `Inner`.
        using var scripts = new ScriptSet();
        scripts.Write("lib.tosh", Library);

        var results = await RunAsync(
            scripts,
            "require Outer.Inner from \"./lib.tosh\"\nInner.F()");

        Assert.Equal("inner-f", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task Importing_the_outer_module_still_works()
    {
        // The form that worked before, and the one a reader may already depend on.
        using var scripts = new ScriptSet();
        scripts.Write("lib.tosh", Library);

        var results = await RunAsync(
            scripts,
            "require Outer from \"./lib.tosh\" as O\nO.Inner.F()");

        Assert.Equal("inner-f", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task A_path_through_a_missing_segment_still_reports_the_export()
    {
        using var scripts = new ScriptSet();
        scripts.Write("lib.tosh", Library);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await RunAsync(
                scripts,
                "require Outer.Nope from \"./lib.tosh\" as N\nN.F()"));

        Assert.Contains("Nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partial_nested_module_imports_by_dotted_path()
    {
        // The reported shape: every level marked partial so it can be extended from
        // another file later. Combines with TS-P2-28's cross-file merging.
        using var scripts = new ScriptSet();
        scripts.Write(
            "shell.tosh",
            """
            partial module ToastLib {
                partial module Shell {
                    export func Ping() -> string { return "pong" }
                }
            }
            """);

        var results = await RunAsync(
            scripts,
            "require ToastLib.Shell from \"./shell.tosh\" as TS\nTS.Ping()");

        Assert.Equal("pong", Assert.Single(results)?.ToString());
    }
}

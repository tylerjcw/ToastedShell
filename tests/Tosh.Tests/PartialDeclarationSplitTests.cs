using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Splitting a <c>partial</c> declaration across imported files, for all four
/// kinds that support it.
/// </summary>
/// <remarks>
/// <para>
/// Found by asking whether modules could be split across files rather than by a
/// failing test. They could — but only through the bare <c>require "./file"</c>
/// form. The named form failed on whichever file came second, in either order:
/// </para>
/// <code>
/// require Sys from "./a.tosh"
/// require Sys from "./b.tosh"
/// ✖ tosh.runtime.require_failed — Export 'Sys' was not found in '…/b.tosh'
/// </code>
/// <para>
/// The diagnostic was actively misleading: the merge had *succeeded*. All four
/// declaration kinds shared the shape <c>existingDef.MergePartial(…); yield
/// break;</c> — merge into the existing declaration, then return before
/// declaring — so the contributing file exported nothing under the name, and the
/// named-import lookup found nothing. The bare form worked only because it never
/// looks a name up.
/// </para>
/// <para>
/// Both forms are covered here, in both orders, because the order determines
/// which file takes the merge path and the two are not symmetric in the code
/// even though they must be in behaviour.
/// </para>
/// </remarks>
public sealed class PartialDeclarationSplitTests
{
    private sealed class ScriptSet : IDisposable
    {
        public ScriptSet()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tosh-partial-split-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string name, string contents) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), contents);

        public string Script(string contents)
        {
            var path = System.IO.Path.Combine(Path, "main.tosh");
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="main"/> with the working directory set to the script
    /// set, since <c>require</c> resolves relative to the requiring script.
    /// </summary>
    private static async Task<IReadOnlyList<object?>> RunAsync(ScriptSet scripts, string main)
    {
        var path = scripts.Script(main);
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = scripts.Path;
        var engine = new ToshEngine(runtime);

        return await engine.ExecuteToListAsync(File.ReadAllText(path), path);
    }

    [Theory]
    // Named on both sides — the form that failed. Listed in both orders because
    // the second file is the one that takes the merge path.
    [InlineData("require Sys from \"./a.tosh\"\nrequire Sys from \"./b.tosh\"")]
    [InlineData("require Sys from \"./b.tosh\"\nrequire Sys from \"./a.tosh\"")]
    // Bare on both sides — the form that already worked, kept so a fix to the
    // named form cannot break it.
    [InlineData("require \"./a.tosh\"\nrequire \"./b.tosh\"")]
    [InlineData("require \"./b.tosh\"\nrequire \"./a.tosh\"")]
    // Mixed.
    [InlineData("require Sys from \"./a.tosh\"\nrequire \"./b.tosh\"")]
    [InlineData("require \"./a.tosh\"\nrequire Sys from \"./b.tosh\"")]
    public async Task A_partial_module_splits_across_imported_files(string imports)
    {
        using var scripts = new ScriptSet();
        scripts.Write("a.tosh", "partial module Sys { export func alpha() -> string { return \"from-a\" } }");
        scripts.Write("b.tosh", "partial module Sys { export func beta() -> string { return \"from-b\" } }");

        var results = await RunAsync(scripts, $"{imports}\nSys.alpha()\nSys.beta()");

        Assert.Equal(["from-a", "from-b"], results.Select(r => r?.ToString()));
    }

    [Theory]
    [InlineData("require Box from \"./a.tosh\"\nrequire Box from \"./b.tosh\"")]
    [InlineData("require Box from \"./b.tosh\"\nrequire Box from \"./a.tosh\"")]
    [InlineData("require \"./a.tosh\"\nrequire \"./b.tosh\"")]
    public async Task A_partial_class_splits_across_imported_files(string imports)
    {
        using var scripts = new ScriptSet();
        scripts.Write("a.tosh", "export partial class Box { func alpha() -> string { return \"from-a\" } }");
        scripts.Write("b.tosh", "export partial class Box { func beta() -> string { return \"from-b\" } }");

        var results = await RunAsync(
            scripts,
            $"{imports}\nvar b = new Box()\n$b.alpha()\n$b.beta()");

        Assert.Equal(["from-a", "from-b"], results.Select(r => r?.ToString()));
    }

    [Fact]
    public async Task A_partial_record_splits_across_imported_files()
    {
        using var scripts = new ScriptSet();
        scripts.Write("a.tosh", "export partial record Pt(x: int)");
        scripts.Write("b.tosh", "export partial record Pt(y: int)");

        var results = await RunAsync(
            scripts,
            "require Pt from \"./a.tosh\"\nrequire Pt from \"./b.tosh\"\n"
            + "var p = new Pt(1, 2)\n$p.x\n$p.y");

        Assert.Equal(["1", "2"], results.Select(r => r?.ToString()));
    }

    [Fact]
    public async Task A_partial_struct_splits_across_imported_files()
    {
        using var scripts = new ScriptSet();
        scripts.Write("a.tosh", "export partial struct Sz(w: int)");
        scripts.Write("b.tosh", "export partial struct Sz(h: int)");

        var results = await RunAsync(
            scripts,
            "require Sz from \"./a.tosh\"\nrequire Sz from \"./b.tosh\"\n"
            + "var s = new Sz(3, 4)\n$s.w\n$s.h");

        Assert.Equal(["3", "4"], results.Select(r => r?.ToString()));
    }

    [Fact]
    public async Task A_partial_module_still_splits_within_one_file()
    {
        // The case that already worked. The shared ModuleExportTable is what makes
        // it work, and the fix must not have replaced sharing with copying.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            """
            partial module Sys { export func alpha() -> string { return "from-a" } }
            partial module Sys { export func beta() -> string { return "from-b" } }
            Sys.alpha()
            Sys.beta()
            """);

        Assert.Equal(["from-a", "from-b"], results.Select(r => r?.ToString()));
    }

    [Fact]
    public async Task A_later_part_can_see_what_an_earlier_part_exported()
    {
        // Prior exports are pre-seeded into the new body's scope, which is what
        // lets a later part build on an earlier one rather than merely sit beside
        // it. Asserted because it is the property that distinguishes merging from
        // two independent declarations that happen to share a name.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            """
            partial module Sys { export func base() -> string { return "base" } }
            partial module Sys { export func wrapped() -> string { return $"[{(base())}]" } }
            Sys.wrapped()
            """);

        Assert.Equal("[base]", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task A_lone_partial_declaration_declares_normally()
    {
        // A partial with nothing to merge into is not an error — the same rule the
        // other three kinds follow, and the one that makes file order irrelevant.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            """
            partial module Sys { export func alpha() -> string { return "only" } }
            Sys.alpha()
            """);

        Assert.Equal("only", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task Extending_a_non_partial_module_is_refused()
    {
        // Modules accepted this silently while classes, records and structs all
        // refused it — the one place the four kinds disagreed.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await engine.ExecuteToListAsync(
                """
                module Sys { export func alpha() -> string { return "a" } }
                partial module Sys { export func beta() -> string { return "b" } }
                """));

        Assert.Contains(
            "tosh.runtime.partial_mismatch",
            error.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public async Task A_plain_redeclaration_replaces_rather_than_merges()
    {
        // Deliberate and consistent across all four kinds, not a defect: a bare
        // redeclaration replaces, which is what a REPL wants. Pinned here so it is
        // not mistaken for the partial-merge bug and "fixed".
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            """
            module Sys { export func alpha() -> string { return "first" } }
            module Sys { export func beta() -> string { return "second" } }
            Sys.beta()
            """);

        Assert.Equal("second", Assert.Single(results)?.ToString());
    }
}

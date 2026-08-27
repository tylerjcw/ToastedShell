using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Introspection reaches a module's exports — <c>TS-P2-68</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reported from use against a real library. <c>ToastLib.Filesystem.GetExtension "&lt;path&gt;"</c>
/// returned a value while <c>help</c> on the same name reported "topic not found", <c>which</c>
/// printed nothing, the bare member name failed too, and <c>help ToastLib</c> had no topic at all
/// — so a library organised as nested modules could not be explored from the shell that ran it.
/// </para>
/// <para>
/// The engine resolves these through <c>TryResolveModuleQualifiedCommand</c>, walking a module's
/// export table and its nested modules. <c>IScopedCommandView</c>, added for <c>TS-P2-54</c>,
/// covered lexical scopes and the global registry and stopped one layer short. It now carries the
/// module exports too, resolved by the engine's own walk rather than a second one written beside
/// it.
/// </para>
/// </remarks>
public sealed class ModuleIntrospectionTests
{
    private const string Library = """
        partial module ToastLib {
            partial module Filesystem {
                ## Gets only the name part of the specified file.
                ## @param=path The path to the file
                export func GetFileName(path: string) -> string => $path

                export func GetExtension(path: string) -> string => $path
            }
        }
        """;

    private static async Task<(ToshEngine Engine, string Path)> WithLibraryAsync(string body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-mod-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "lib.tosh").Replace("\\", "/");
        await File.WriteAllTextAsync(path, body);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = directory;
        var engine = new ToshEngine(runtime.Language);
        await engine.ExecuteToListAsync($"require \"{path}\"");

        return (engine, directory);
    }

    private static async Task<T> UsingLibraryAsync<T>(string body, Func<ToshEngine, Task<T>> probe)
    {
        var (engine, directory) = await WithLibraryAsync(body);

        try
        {
            return await probe(engine);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── The reported case ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_module_function_is_callable_and_findable()
    {
        await UsingLibraryAsync(Library, async engine =>
        {
            // Half one: it really is callable, or the rest of this proves nothing.
            var called = await engine.ExecuteToListAsync("ToastLib.Filesystem.GetExtension \"a.txt\"");
            Assert.Equal("a.txt", called[0]?.ToString());

            var topic = HelpCatalog.ResolveTopic(
                engine.Shell(),
                "ToastLib.Filesystem.GetFileName",
                engine.CreateScopedCommandView());

            Assert.NotNull(topic);
            Assert.Equal("ToastLib.Filesystem.GetFileName", topic!.Name);
            Assert.Contains("name part", topic.Description, StringComparison.OrdinalIgnoreCase);
            return 0;
        });
    }

    [Fact]
    public async Task The_doc_comment_reaches_the_topic()
    {
        await UsingLibraryAsync(Library, engine =>
        {
            var topic = HelpCatalog.ResolveTopic(
                engine.Shell(),
                "ToastLib.Filesystem.GetFileName",
                engine.CreateScopedCommandView());

            var argument = Assert.Single(topic!.Arguments!);
            Assert.Equal("path", argument.Name);
            Assert.Equal("The path to the file", argument.Description);
            return Task.FromResult(0);
        });
    }

    // ── A bare member name, when it is unambiguous ─────────────────────────────

    [Fact]
    public async Task A_bare_member_name_resolves_when_only_one_module_exports_it()
    {
        await UsingLibraryAsync(Library, engine =>
        {
            var topic = HelpCatalog.ResolveTopic(engine.Shell(), "GetFileName", engine.CreateScopedCommandView());

            Assert.NotNull(topic);
            Assert.Equal("ToastLib.Filesystem.GetFileName", topic!.Name);
            return Task.FromResult(0);
        });
    }

    [Fact]
    public async Task A_bare_name_two_modules_share_is_not_guessed_at()
    {
        // Choosing one silently would answer a question the caller did not ask. Qualifying is
        // always available, so refusing is the honest answer.
        const string ambiguous = """
            partial module Alpha { export func Shared() => 1 }
            partial module Beta  { export func Shared() => 2 }
            """;

        await UsingLibraryAsync(ambiguous, engine =>
        {
            var view = engine.CreateScopedCommandView();

            Assert.False(view.TryGet("Shared", out _));
            Assert.True(view.TryGet("Alpha.Shared", out _));
            Assert.True(view.TryGet("Beta.Shared", out _));

            // Neither topic claims the bare name as an alias.
            var alpha = HelpCatalog.ResolveTopic(engine.Shell(), "Alpha.Shared", view);
            Assert.DoesNotContain("Shared", alpha!.Aliases, StringComparer.Ordinal);
            return Task.FromResult(0);
        });
    }

    // ── Modules have topics of their own ───────────────────────────────────────

    [Fact]
    public async Task A_module_has_a_topic_listing_its_exports()
    {
        await UsingLibraryAsync(Library, engine =>
        {
            var view = engine.CreateScopedCommandView();
            var topic = HelpCatalog.ResolveTopic(engine.Shell(), "ToastLib.Filesystem", view);

            Assert.NotNull(topic);
            Assert.Equal("Modules", topic!.Category);
            Assert.Contains("GetFileName", topic.Arguments!.Select(a => a.Name), StringComparer.Ordinal);
            Assert.Contains("GetExtension", topic.Arguments!.Select(a => a.Name), StringComparer.Ordinal);
            return Task.FromResult(0);
        });
    }

    [Fact]
    public async Task An_enclosing_module_lists_the_one_nested_in_it()
    {
        await UsingLibraryAsync(Library, engine =>
        {
            var topic = HelpCatalog.ResolveTopic(engine.Shell(), "ToastLib", engine.CreateScopedCommandView());

            Assert.NotNull(topic);
            Assert.Contains("ToastLib.Filesystem", topic!.Notes ?? string.Empty, StringComparison.Ordinal);
            return Task.FromResult(0);
        });
    }

    // ── The view itself ────────────────────────────────────────────────────────

    [Fact]
    public async Task Module_exports_appear_in_the_full_listing()
    {
        // So `help topics`, `apropos` and completion see them, not only a direct lookup.
        await UsingLibraryAsync(Library, engine =>
        {
            var view = engine.CreateScopedCommandView();
            var qualified = view.QualifiedCommands.Select(pair => pair.Key).ToArray();

            Assert.Contains("ToastLib.Filesystem.GetFileName", qualified, StringComparer.Ordinal);
            Assert.Equal(2, view.QualifiedCommands.Count);
            return Task.FromResult(0);
        });
    }

    [Fact]
    public void The_global_registry_reports_no_modules()
    {
        // The default on the interface: a registry has none, so nothing else has to change.
        // Reached through the interface, since these are default implementations rather than
        // members of the registry itself — which is the point: nothing else had to change.
        IScopedCommandView registry = ToshRuntime.CreateDefault().Commands;

        Assert.Empty(registry.Modules);
        Assert.Empty(registry.QualifiedCommands);
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Fact]
    public void A_builtin_topic_is_unchanged()
    {
        var runtime = ToshRuntime.CreateDefault();
        var topic = HelpCatalog.ResolveTopic(runtime, "ls");

        Assert.NotNull(topic);
        Assert.Equal("ls", topic!.Name);
    }

    [Fact]
    public async Task A_script_declared_function_is_still_found_by_its_own_name()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        await engine.ExecuteToListAsync("## Local.\nfunc localfn() { return 1 }");

        var topic = HelpCatalog.ResolveTopic(runtime, "localfn", engine.CreateScopedCommandView());

        Assert.NotNull(topic);
        Assert.Equal("localfn", topic!.Name);
    }
}

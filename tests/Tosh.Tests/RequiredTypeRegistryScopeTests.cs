using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>Partial modules share exported names, never body-private lexical state.</summary>
public sealed class RequiredTypeRegistryScopeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tosh-required-type-scope-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativePath, string source)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }

    private (RequiredTypeRegistry Registry, ModuleDefinitionStatementSyntax[] Bodies) Discover(string source)
    {
        var path = Write("client.tosh", source);
        var parsed = ToshParser.Parse(source, path);
        Assert.Empty(parsed.Diagnostics);
        var script = Assert.IsType<ScriptStatementSyntax>(parsed.Statement);
        return (new RequiredTypeRegistry(parsed), script.Statements.OfType<ModuleDefinitionStatementSyntax>().ToArray());
    }

    private async Task<string> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = _root;
        var engine = new ToshEngine(runtime.Language);
        var values = await engine.ExecuteToListAsync(source, Path.Combine(_root, "client.tosh"));
        return string.Join(",", values.Select(value => value?.ToString() ?? "null"));
    }

    [Fact]
    public void A_later_partial_body_inherits_exports_but_not_private_types()
    {
        var (registry, bodies) = Discover("""
            partial module Api {
                class Shared { }
                shy class Hidden { }
            }
            partial module Api {
                class Later { }
            }
            """);

        var first = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[0]));
        var second = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[1]));
        Assert.IsType<UserClassType>(first.Resolve("Hidden"));
        Assert.IsType<UserClassType>(second.Resolve("Shared"));
        Assert.True(second.Resolve("Hidden").IsDynamic);
        Assert.Contains("Api.Shared", registry.Types.Keys);
        Assert.Contains("Api.Later", registry.Types.Keys);
        Assert.DoesNotContain("Api.Hidden", registry.Types.Keys);
    }

    [Fact]
    public void Private_requires_stay_with_the_declaring_body_and_its_member_annotations()
    {
        Write("dependency.tosh", "export class Implementation { }\n");
        var (registry, bodies) = Discover("""
            partial module Api {
                shy require "./dependency.tosh"
                class Factory {
                    func Create() -> Implementation { return new Implementation() }
                }
            }
            partial module Api {
                class Later { }
            }
            """);

        var first = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[0]));
        var second = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[1]));
        var implementation = Assert.IsType<UserClassType>(first.Resolve("Implementation"));
        Assert.True(second.Resolve("Implementation").IsDynamic);
        Assert.DoesNotContain("Api.Implementation", registry.Types.Keys);
        Assert.Same(implementation, registry.ResolveMember(registry.Types["Api.Factory"], "Implementation", second));
    }

    [Fact]
    public void A_later_partial_body_does_not_inherit_private_nested_modules()
    {
        var (registry, bodies) = Discover("""
            partial module Api {
                shy module Hidden { class Secret { } }
                module Public { class Shared { } }
            }
            partial module Api { class Later { } }
            """);

        var first = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[0]));
        var second = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[1]));
        Assert.IsType<UserClassType>(first.Resolve("Hidden.Secret"));
        Assert.True(second.Resolve("Hidden.Secret").IsDynamic);
        Assert.IsType<UserClassType>(second.Resolve("Public.Shared"));
        Assert.DoesNotContain("Api.Hidden.Secret", registry.Types.Keys);
    }

    [Fact]
    public void Existing_module_aliases_observe_later_public_contributions_only()
    {
        Write("base.tosh", "partial module Api { class First { } }\n");
        var (registry, _) = Discover("""
            require Api from "./base.tosh" as Original
            require Api from "./base.tosh" as Renamed
            partial module Renamed {
                class Later { }
                shy class Hidden { }
            }
            """);

        Assert.Same(registry.Types["Original.First"], registry.Types["Renamed.First"]);
        Assert.Same(registry.Types["Original.Later"], registry.Types["Renamed.Later"]);
        Assert.DoesNotContain("Original.Hidden", registry.Types.Keys);
        Assert.DoesNotContain("Renamed.Hidden", registry.Types.Keys);
    }

    [Fact]
    public async Task Lexical_private_bindings_survive_but_member_annotations_prefer_current_exports()
    {
        const string source = """
            partial module Api {
                shy class Item { prop Value: int = 1 }
                class Factory { func Create(value) -> Item { return $value } }
            }
            partial module Api {
                class Item { prop Value: string = "public" }
            }
            """;
        var (registry, bodies) = Discover(source);

        var first = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[0]));
        var second = Assert.IsType<TypeNameResolver>(registry.ResolverFor(bodies[1]));
        var privateType = Assert.IsType<UserClassType>(first.Resolve("Item"));
        var publicType = Assert.IsType<UserClassType>(second.Resolve("Item"));
        Assert.NotSame(privateType, publicType);
        Assert.Same(publicType, registry.Types["Api.Item"]);
        Assert.Same(publicType, registry.ResolveMember(registry.Types["Api.Factory"], "Item", second));
        Assert.Equal("public", await RunAsync(source + "\n(new Api.Factory()).Create(new Api.Item()).Value"));
    }

    [Fact]
    public void Earlier_body_member_annotations_can_see_later_public_contributions()
    {
        var (registry, _) = Discover("""
            partial module Api {
                class Factory { func Create() -> Later { return new Later() } }
            }
            partial module Api { class Later { } }
            """);

        var caller = new TypeNameResolver(userTypes: registry.Types);
        Assert.Same(registry.Types["Api.Later"], registry.ResolveMember(registry.Types["Api.Factory"], "Later", caller));
    }

    [Fact]
    public async Task Partial_bodies_snapshot_prior_public_bindings_for_lexical_resolution()
    {
        const string source = """
            partial module Api { class Item { prop Value: int = 1 } }
            partial module Api {
                class Factory { func Read() { return (new Item()).Value } }
            }
            partial module Api { class Item { prop Value: int = 2 } }
            """;
        var (registry, bodies) = Discover(source);

        var original = registry.ResolverFor(bodies[0])!.Resolve("Item");
        var middle = registry.ResolverFor(bodies[1])!.Resolve("Item");
        var latest = registry.ResolverFor(bodies[2])!.Resolve("Item");
        Assert.Same(original, middle);
        Assert.NotSame(middle, latest);
        Assert.Same(latest, registry.Types["Api.Item"]);
        Assert.Equal("1,2", await RunAsync(source + "\n(new Api.Factory()).Read()\n(new Api.Item()).Value"));
    }

    [Fact]
    public async Task Nested_partial_modules_merge_exports_from_prior_outer_bodies()
    {
        const string source = """
            partial module Api {
                partial module Nested { class First { prop Value: int = 1 } }
            }
            partial module Api {
                partial module Nested { class Second { prop Value: int = 2 } }
            }
            """;
        var (registry, _) = Discover(source);

        Assert.IsType<UserClassType>(registry.Types["Api.Nested.First"]);
        Assert.IsType<UserClassType>(registry.Types["Api.Nested.Second"]);
        Assert.Equal("1,2", await RunAsync(source + "\n(new Api.Nested.First()).Value\n(new Api.Nested.Second()).Value"));
    }

    [Fact]
    public async Task A_nested_partial_declaration_can_extend_an_outer_visible_module()
    {
        const string source = """
            partial module Shared { class First { prop Value: int = 1 } }
            module Outer {
                partial module Shared { class Second { prop Value: int = 2 } }
            }
            """;
        var (registry, _) = Discover(source);

        Assert.Same(registry.Types["Shared.First"], registry.Types["Outer.Shared.First"]);
        Assert.Same(registry.Types["Shared.Second"], registry.Types["Outer.Shared.Second"]);
        Assert.Equal("1,2", await RunAsync(source + "\n(new Outer.Shared.First()).Value\n(new Shared.Second()).Value"));
    }

    [Fact]
    public void A_local_module_shadows_the_entire_parent_module_path()
    {
        var (registry, bodies) = Discover("""
            module Shared { class Outside { } }
            module Outer {
                shy module Shared { class Inside { } }
                class Public { }
            }
            """);

        var inner = registry.ResolverFor(bodies[1])!;
        Assert.IsType<UserClassType>(inner.Resolve("Shared.Inside"));
        Assert.True(inner.Resolve("Shared.Outside").IsDynamic);
        Assert.Contains("Shared.Outside", registry.Types.Keys);
        Assert.DoesNotContain("Outer.Shared.Inside", registry.Types.Keys);
        Assert.DoesNotContain("Inside", registry.Types.Keys);
        Assert.DoesNotContain("Public", registry.Types.Keys);
    }

    [Fact]
    public void Compiler_lowering_does_not_restore_a_shadowed_parent_module_type()
    {
        const string source = """
            module Shared { class Outside { } }
            module Outer {
                shy module Shared { class Inside { } }
                var visible = new Shared.Inside()
                var hidden: Shared.Outside = new Shared.Outside()
            }
            """;
        var path = Write("client.tosh", source);
        var parse = ToshParser.Parse(source, path);
        Assert.Empty(parse.Diagnostics);
        var runtime = ToshRuntime.CreateDefault();
        var unit = Lowerer.Lower(parse, runtime.Commands, resolveRequiredTypes: true);

        Assert.IsType<UserClassType>(unit.Symbols.Single(symbol => symbol.Name == "visible").DeclaredType);
        Assert.True(unit.Symbols.Single(symbol => symbol.Name == "hidden").DeclaredType.IsDynamic);
        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("Shared.Outside", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Member_annotations_keep_the_original_module_path_through_an_alias()
    {
        Write("library.tosh", """
            module Original {
                class Item { prop Value: int = 7 }
                class Factory { func Create() -> Original.Item { return new Item() } }
            }
            """);
        const string source = """
            require Original from "./library.tosh" as Renamed
            module Original { class Item { prop Value: string = "caller" } }
            """;
        var (registry, _) = Discover(source);
        var caller = new TypeNameResolver(userTypes: registry.Types);

        Assert.NotSame(registry.Types["Original.Item"], registry.Types["Renamed.Item"]);
        Assert.Same(registry.Types["Renamed.Item"],
            registry.ResolveMember(registry.Types["Renamed.Factory"], "Original.Item", caller));
        Assert.Equal("7", await RunAsync(source + "\n(new Renamed.Factory()).Create().Value"));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public void Nested_modules_receive_the_same_compile_annotation_audit(bool allowDynamic, int expectedCount)
    {
        var parse = ToshParser.Parse("""
            module Outer {
                module Inner {
                    func Unannotated(value) { return $value }
                    var unknown = new UnprovidedModuleAuditType()
                }
            }
            """);
        Assert.Empty(parse.Diagnostics);
        var runtime = ToshRuntime.CreateDefault();
        var unit = Lowerer.Lower(parse, runtime.Commands, resolveRequiredTypes: true);

        var diagnostics = TypeChecker.CheckCompileAnnotations(unit, allowDynamic);
        Assert.Equal(expectedCount, diagnostics.Count);
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Code == "tosh.compile.missing_type_annotation"));
        Assert.Equal(allowDynamic ? 0 : 1, diagnostics.Count(diagnostic => diagnostic.Code == "tosh.compile.implicit_dynamic"));
    }

    [Theory]
    [InlineData("<input>")]
    [InlineData("repl_entry_1")]
    public void Compiler_module_scopes_retain_ambient_types_for_virtual_sources(string sourceName)
    {
        var ambient = ToshParser.Parse("class AmbientProbeType { }");
        var parse = ToshParser.Parse("""
            module Client { var value: AmbientProbeType = new AmbientProbeType() }
            """, sourceName);
        Assert.Empty(parse.Diagnostics);
        var runtime = ToshRuntime.CreateDefault();
        var unit = Lowerer.Lower(parse, runtime.Commands, ambient.Statement, resolveRequiredTypes: true);

        Assert.IsType<UserClassType>(unit.Symbols.Single(symbol => symbol.Name == "value").DeclaredType);
        Assert.Empty(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
    }
}

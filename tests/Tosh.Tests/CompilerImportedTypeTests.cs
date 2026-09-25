using System.Reflection;
using Tosh.Compiler;
using Tosh.Compiler.IR;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A required library contributes type metadata without being executed by the compiler.
/// These use real, source-relative files and the normal annotation audit: allowing dynamic
/// would hide the missing imported-type information that motivated this regression suite.
/// </summary>
[Collection(ConsoleSerialCollection.Name)]
public sealed class CompilerImportedTypeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tosh-compile-import-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private BoundUnit Lower(
        string source,
        string relativePath = "client.tosh",
        bool resolveRequiredTypes = true)
    {
        var path = Write(relativePath, source);
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = _root;
        var engine = new ToshEngine(runtime.Language);
        var parse = engine.Parse(source, path);
        Assert.True(parse.Diagnostics.Count == 0,
            $"parse errors: {string.Join(Environment.NewLine, parse.Diagnostics)}");
        return resolveRequiredTypes
            ? Lowerer.Lower(parse, runtime.Commands, resolveRequiredTypes: true)
            : Lowerer.Lower(parse, runtime.Commands);
    }

    private static void AssertStrictlyTyped(BoundUnit unit)
    {
        var typeDiagnostics = TypeChecker.Check(unit);
        Assert.True(typeDiagnostics.Count == 0,
            $"type check errors: {string.Join(Environment.NewLine, typeDiagnostics.Select(d => d.Title))}");
        var diagnostics = TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false);
        Assert.True(diagnostics.Count == 0,
            $"strict compile errors: {string.Join(Environment.NewLine, diagnostics.Select(d => d.Title))}");
    }

    private static byte[] Emit(BoundUnit unit, string assemblyName)
    {
        AssertStrictlyTyped(unit);
        using var stream = new MemoryStream();
        // A require remains runtime-assisted. This does not claim that imported library
        // bodies have been translated into pure IL or that the Pure profile supports them.
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream, CompileProfile.Permissive);
        Assert.True(result.IsClean,
            $"unsupported shapes: {string.Join(Environment.NewLine, result.UnsupportedShapes)}");
        return stream.ToArray();
    }

    private static string Run(BoundUnit unit)
    {
        var assemblyName = $"ImportedTypes_{Guid.NewGuid():N}";
        var assembly = Assembly.Load(Emit(unit, assemblyName));
        var program = assembly.GetType($"{assemblyName}.Program");
        Assert.NotNull(program);
        var main = program.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(main);

        var originalOut = Console.Out;
        using var capture = new StringWriter();
        Console.SetOut(capture);
        try
        {
            main.Invoke(null, [Array.Empty<string>()]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return capture.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private const string Library = """
        export module RequiredTypeProbe {
            export class Sample {
                proud prop Value: int = 7
                proud func Read() -> int { return 7 }
            }
        }
        """;

    [Theory]
    [InlineData("shy bind native \"libc.so.6\" { func abs(number: int) -> int }")]
    [InlineData("shy raw func abs(number: int) -> int from \"libc.so.6\"")]
    public void Imported_native_wrappers_do_not_expose_private_bindings(string binding)
    {
        if (!OperatingSystem.IsLinux()) return;
        var nativePath = Write("native.tosh", $$"""
            export module NativePrivacy {
                export hermit class Magnitudes {
                    {{binding}}
                    proud shared func Read(n: int) -> int { return Magnitudes.abs($n) }
                }
            }
            """);
        var unit = Lower($$"""
            require "{{nativePath.Replace("\\", "/")}}"
            var magnitude: int = NativePrivacy.Magnitudes.Read(-7)
            assert ($magnitude == 7)
            var blocked: bool = false
            try { var leaked: int = NativePrivacy.Magnitudes.abs(-9) } catch { $blocked = true }
            assert $blocked "Private native call must be refused"
            var again: int = NativePrivacy.Magnitudes.Read(-11)
            assert ($again == 11)
            writeline "native privacy preserved"
            """);

        Assert.Equal("native privacy preserved", Run(unit));
    }

    [Fact]
    public void Ordinary_lowering_does_not_discover_types_in_required_files()
    {
        Write("library.tosh", Library);
        var unit = Lower("""
            require "./library.tosh"
            var sample = new RequiredTypeProbe.Sample()
            """, resolveRequiredTypes: false);

        // The interpreter and editor use ordinary lowering frequently. File-system
        // metadata discovery is an explicit compiler opt-in, not their new default.
        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("sample", diagnostic.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var sample = new RequiredTypeProbe.Sample()")]
    [InlineData("var sample: RequiredTypeProbe.Sample = new RequiredTypeProbe.Sample()")]
    public void Imported_class_and_its_declared_members_compile_without_allow_dynamic(string declaration)
    {
        Write("library.tosh", Library);
        var unit = Lower($$"""
            require "./library.tosh"
            {{declaration}}
            var number = $sample.Read()
            var property = $sample.Value
            echo $number
            echo $property
            """);

        Assert.Equal("7\n7", Run(unit));
    }

    [Theory]
    [InlineData("require RequiredTypeProbe from \"./library.tosh\" as Probe", "Probe.Sample")]
    [InlineData("require RequiredTypeProbe.Sample from \"./library.tosh\"", "Sample")]
    [InlineData("require RequiredTypeProbe.Sample from \"./library.tosh\" as Selected", "Selected")]
    public void Selective_imports_and_aliases_use_the_imported_name(string require, string typeName)
    {
        Write("library.tosh", Library);
        var unit = Lower($$"""
            {{require}}
            var sample: {{typeName}} = new {{typeName}}()
            var number = $sample.Read()
            echo $number
            """);

        Assert.Equal("7", Run(unit));
    }

    [Fact]
    public void Source_relative_transitive_imports_preserve_the_public_factory_contract()
    {
        Write("package/contracts.tosh", """
            export hollow class Axis {
                proud hollow prop Limit: int { get { } }
            }
            export hollow class Axes {
                proud hollow prop XAxis: Api.Axis { get { } }
            }
            """);
        Write("package/model.tosh", """
            shy require "./contracts.tosh"
            shy class AxisImpl extends Axis {
                proud overrule prop Limit: int { get { return 7 } }
            }
            shy class AxesImpl extends Axes {
                proud overrule prop XAxis: Api.Axis { get { return new AxisImpl() } }
            }
            export class Figure {
                proud func Subplots() -> Api.Axes { return new AxesImpl() }
            }
            """);
        Write("package/entry.tosh", """
            export module Api {
                require "./contracts.tosh"
                require Figure from "./model.tosh"
            }
            """);
        var unit = Lower("""
            require "../package/entry.tosh"
            var figure = new Api.Figure()
            var axes = $figure.Subplots()
            var axis = $axes.XAxis
            var limit = $axis.Limit
            echo $limit
            """, "client/main.tosh");

        Assert.Equal("7", Run(unit));
    }

    [Theory]
    [InlineData("RequiredTypeProbe.Hidden")]
    [InlineData("RequiredTypeProbe.Missing")]
    [InlineData("Sample")]
    public void Unexported_missing_or_unqualified_types_do_not_become_known(string typeName)
    {
        Write("library.tosh", """
            export module RequiredTypeProbe {
                export class Sample { }
                shy class Hidden { }
            }
            """);
        var unit = Lower($$"""
            require "./library.tosh"
            var value: {{typeName}} = new {{typeName}}()
            """);

        var diagnostics = TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false);
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "tosh.compile.implicit_dynamic"
            && diagnostic.Title.Contains(typeName, StringComparison.Ordinal));
    }

    [Fact]
    public void Private_require_dependencies_are_not_reexported_as_public_types()
    {
        Write("package/dependency.tosh", "export class Implementation { }\n");
        Write("package/entry.tosh", """
            export module Api {
                shy require "./dependency.tosh"
                export class Public { }
            }
            """);
        var unit = Lower("""
            require "./package/entry.tosh"
            var publicValue = new Api.Public()
            var hiddenValue: Api.Implementation = new Api.Implementation()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("Api.Implementation", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Selective_import_does_not_make_other_exported_types_visible()
    {
        Write("library.tosh", "export class Selected { }\nexport class Other { }\n");
        var unit = Lower("""
            require Selected from "./library.tosh"
            var selected = new Selected()
            var other: Other = new Other()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("Other", diagnostic.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class Hidden { }", "Hidden")]
    [InlineData("shy module Private { export class Hidden { } }", "Private.Hidden")]
    [InlineData("module Outer { shy module Private { class Hidden { } } }", "Outer.Private.Hidden")]
    public void File_default_declarations_and_private_module_paths_do_not_leak(
        string hiddenDeclaration,
        string hiddenType)
    {
        Write("library.tosh", $"{hiddenDeclaration}\nexport class Public {{ }}\n");
        var unit = Lower($$"""
            require "./library.tosh"
            var visible = new Public()
            var hidden: {{hiddenType}} = new {{hiddenType}}()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains(hiddenType, diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_root_require_does_not_implicitly_reexport_its_imports()
    {
        Write("package/inner.tosh", "export class Inner { }\n");
        Write("package/entry.tosh", """
            require "./inner.tosh"
            export class Public { }
            """);
        var unit = Lower("""
            require "./package/entry.tosh"
            var visible = new Public()
            var hidden: Inner = new Inner()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("Inner", diagnostic.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("require RequiredTypeProbe from \"./library.tosh\" as Probe", "Probe.Sample", "RequiredTypeProbe.Sample")]
    [InlineData("require RequiredTypeProbe.Sample from \"./library.tosh\" as Selected", "Selected", "RequiredTypeProbe.Sample")]
    [InlineData("require RequiredTypeProbe.Sample from \"./library.tosh\" as Selected", "Selected", "Sample")]
    public void An_alias_does_not_also_import_the_original_name(
        string require,
        string visibleType,
        string originalType)
    {
        Write("library.tosh", Library);
        var unit = Lower($$"""
            {{require}}
            var visible = new {{visibleType}}()
            var hidden: {{originalType}} = new {{originalType}}()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains(originalType, diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_module_and_its_default_members_are_exported()
    {
        // Required-file roots do not export ordinary declarations by default, but module
        // declarations are special: the module itself and its body are public by default.
        Write("library.tosh", """
            module Api {
                class Sample { prop Value: int = 7 }
            }
            """);
        var unit = Lower("""
            require "./library.tosh"
            var sample = new Api.Sample()
            var number = $sample.Value
            echo $number
            """);

        Assert.Equal("7", Run(unit));
    }

    [Fact]
    public void Extensionless_requires_resolve_to_source_relative_tosh_files()
    {
        Write("package/library.tosh", Library);
        var unit = Lower("""
            require "../package/library"
            var sample = new RequiredTypeProbe.Sample()
            var number = $sample.Read()
            echo $number
            """, "client/main.tosh");

        Assert.Equal("7", Run(unit));
    }

    [Fact]
    public void A_require_cycle_terminates_during_metadata_discovery()
    {
        Write("package/first.tosh", "require \"./second.tosh\"\nexport class First { }\n");
        Write("package/second.tosh", "require \"./first.tosh\"\nexport class Second { }\n");

        var unit = Lower("""
            require "./package/first.tosh"
            var localValue = 7
            """);

        // Runtime require owns the circular-import error. Metadata discovery must neither
        // execute that import nor recurse forever; it does not make this program runnable.
        AssertStrictlyTyped(unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("class Sibling { prop Value: string = \"caller\" }")]
    public void Imported_member_annotations_resolve_in_the_declaring_module(
        string callerDeclaration)
    {
        Write("library.tosh", """
            module Original {
                class Sibling { prop Value: int = 7 }
                class Factory {
                    func Create() -> Sibling { return new Sibling() }
                }
            }
            """);
        var unit = Lower($$"""
            require Original from "./library.tosh" as Imported
            {{callerDeclaration}}
            var factory = new Imported.Factory()
            var item = $factory.Create()
            var number = $item.Value
            echo $number
            """);

        AssertStrictlyTyped(unit);
        Assert.Equal(typeof(int), unit.Symbols.Single(symbol => symbol.Name == "number").DeclaredType.ClrType);
        Assert.Equal("7", Run(unit));
    }

    [Fact]
    public void Missing_imported_member_annotations_do_not_borrow_unrelated_caller_types()
    {
        Write("library.tosh", """
            export class Factory {
                func Create() -> Unprovided { return null }
            }
            """);
        var unit = Lower("""
            require "./library.tosh"
            class Unprovided { }
            var factory = new Factory()
            var missing = $factory.Create()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("missing", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_metadata_does_not_invent_types_for_unannotated_member_returns()
    {
        Write("library.tosh", "export class Sample { proud func Read() { return 7 } }\n");
        var unit = Lower("""
            require "./library.tosh"
            var sample = new Sample()
            var unknown = $sample.Read()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("unknown", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Lowering_and_emission_do_not_execute_the_imported_library()
    {
        var markerPath = Path.Combine(_root, "executed.txt");
        Write("library.tosh", $$"""
            System.IO.File.AppendAllText('{{markerPath}}', "loaded\n")
            export class Sample { proud func Read() -> int { return 7 } }
            """);
        var unit = Lower("""
            require "./library.tosh"
            require "./library.tosh"
            var sample = new Sample()
            var number = $sample.Read()
            echo $number
            """);

        Assert.False(File.Exists(markerPath));
        var bytes = Emit(unit, $"MetadataOnly_{Guid.NewGuid():N}");
        Assert.NotEmpty(bytes);
        Assert.False(File.Exists(markerPath));

        Assert.Equal("7", Run(unit));
        Assert.Equal("loaded\n", File.ReadAllText(markerPath));
    }

    [Fact]
    public void Missing_import_is_not_replaced_with_a_concrete_placeholder_type()
    {
        // "Missing" is not an unknown type: the platform index can resolve it to
        // System.Reflection.Missing. Use a sentinel that cannot collide with that lookup.
        var unit = Lower("""
            require "./absent.tosh"
            var value: CompilerImportMissingProbe = new CompilerImportMissingProbe()
            """);

        var diagnostic = Assert.Single(TypeChecker.CheckCompileAnnotations(unit, allowDynamic: false));
        Assert.Equal("tosh.compile.implicit_dynamic", diagnostic.Code);
        Assert.Contains("CompilerImportMissingProbe", diagnostic.Title, StringComparison.Ordinal);
    }
}

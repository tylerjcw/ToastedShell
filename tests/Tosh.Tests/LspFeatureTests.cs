using Tosh.LanguageServices;

namespace Tosh.Tests;

public sealed class LspFeatureTests
{
    private readonly ToshLanguageFeatures _features = new();
    private static readonly string FixtureProjectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Tosh.LspFixture/Tosh.LspFixture.csproj"));
    private static readonly string FixtureDocumentUri = new Uri(Path.Combine(Path.GetTempPath(), "tosh-lsp-fixture-test.tosh")).AbsoluteUri;
    private static readonly Lazy<string> FixtureAssemblyPath = new(EnsureFixtureAssemblyBuilt);

    [Fact]
    public void Diagnostics_surface_parser_errors()
    {
        var diagnostics = _features.GetDiagnostics("var = 1", "test.tosh");

        var diagnostic = Assert.Single(diagnostics);
        Assert.StartsWith("tosh::parser::", diagnostic.Code, StringComparison.Ordinal);
        Assert.Equal("tosh", diagnostic.Source);
    }

    [Fact]
    public void Completion_items_include_declared_variables_keywords_and_builtins()
    {
        const string script = """
            var person
            $
            """;

        var items = _features.GetCompletionItems(script, new LspPosition(1, 1));

        Assert.Contains(items, item => item.Label == "$person");
        Assert.Contains(items, item => item.Label == "$tosh");
        Assert.Contains(items, item => item.Label == "$env");

        var rootItems = _features.GetCompletionItems("wh", new LspPosition(0, 2));
        Assert.Contains(rootItems, item => item.Label == "while");
        Assert.Contains(rootItems, item => item.Label == "where");
    }

    [Fact]
    public void Completion_items_use_visible_scope_for_variables_functions_and_classes()
    {
        const string script = """
            var globalName = "toast"
            class Item { }
            func greet(name) {
                var localName = "hello"
                $
                gr
                It
            }
            """;

        var variableItems = _features.GetCompletionItems(script, new LspPosition(4, 5));
        var functionItems = _features.GetCompletionItems(script, new LspPosition(5, 6));
        var classItems = _features.GetCompletionItems(script, new LspPosition(6, 6));

        Assert.Contains(variableItems, item => item.Label == "$globalName");
        Assert.Contains(variableItems, item => item.Label == "$localName");
        Assert.Contains(variableItems, item => item.Label == "$name");
        Assert.Contains(functionItems, item => item.Label == "greet");
        Assert.Contains(classItems, item => item.Label == "Item");
    }

    [Fact]
    public void Completion_items_group_top_level_function_overloads()
    {
        const string script = """
            func greet() { echo noargs }
            func greet(name: string) { echo hello $name }
            gr
            """;

        var items = _features.GetCompletionItems(script, new LspPosition(2, 2));
        var greet = Assert.Single(items, item => item.Label == "greet");

        Assert.Equal("Function (2 overloads)", greet.Detail);
        Assert.Contains("func greet()", greet.Documentation, StringComparison.Ordinal);
        Assert.Contains("func greet(name: string)", greet.Documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void Completion_items_include_clr_namespaces_imported_types_aliases_and_members()
    {
        const string script = """
            using System.
            using System.Drawing
            using System.IO = IO
            var pt = new Point(2, 2)
            $pt.
            IO.
            Point.
            """;

        var usingItems = _features.GetCompletionItems(script, new LspPosition(0, 13));
        var importedTypeItems = _features.GetCompletionItems(script, new LspPosition(3, 15));
        var pointMemberItems = _features.GetCompletionItems(script, new LspPosition(4, 4));
        var aliasItems = _features.GetCompletionItems(script, new LspPosition(5, 3));
        var staticItems = _features.GetCompletionItems(script, new LspPosition(6, 6));

        Assert.Contains(usingItems, item => item.Label == "Drawing");
        Assert.Contains(importedTypeItems, item => item.Label == "Point");
        Assert.Contains(pointMemberItems, item => item.Label == "X");
        Assert.Contains(pointMemberItems, item => item.Label == "Y");
        Assert.Contains(aliasItems, item => item.Label == "Path");
        Assert.Contains(staticItems, item => item.Label == "Empty");
    }

    [Fact]
    public void Completion_items_include_members_for_typed_parameters()
    {
        const string script = """
            func paint(color: System.Drawing.Color) {
                $color.
            }
            """;

        var items = _features.GetCompletionItems(script, new LspPosition(1, 11));

        Assert.Contains(items, item => item.Label == "Name");
        Assert.Contains(items, item => item.Label == "A");
    }

    [Fact]
    public void Completion_items_include_tosh_class_members_and_this_context()
    {
        var (instanceText, instancePosition) = ExtractCursor("""
            class Item {
                prop Name: string? = null
                shy prop InternalName: string? = null

                Item() { }

                static func named(name: string) -> Item {
                    return new Item()
                }

                func describe() -> string {
                    return $this.Name
                }

                shy func is_low_stock() -> bool {
                    return false
                }
            }

            var item = new Item()
            $item.¦
            """);
        var (thisText, thisPosition) = ExtractCursor("""
            class Item {
                prop Name: string? = null
                shy prop InternalName: string? = null

                func describe() -> string {
                    $this.¦
                    return $this.Name
                }

                shy func is_low_stock() -> bool {
                    return false
                }
            }
            """);
        var (staticText, staticPosition) = ExtractCursor("""
            class Item {
                static func named(name: string) -> Item {
                    return new Item()
                }
            }

            Item.¦
            """);

        var instanceItems = _features.GetCompletionItems(instanceText, instancePosition);
        var thisItems = _features.GetCompletionItems(thisText, thisPosition);
        var staticItems = _features.GetCompletionItems(staticText, staticPosition);

        Assert.Contains(instanceItems, item => item.Label == "Name");
        Assert.Contains(instanceItems, item => item.Label == "describe");
        Assert.DoesNotContain(instanceItems, item => item.Label == "InternalName");
        Assert.DoesNotContain(instanceItems, item => item.Label == "is_low_stock");

        Assert.Contains(thisItems, item => item.Label == "Name");
        Assert.Contains(thisItems, item => item.Label == "InternalName");
        Assert.Contains(thisItems, item => item.Label == "describe");
        Assert.Contains(thisItems, item => item.Label == "is_low_stock");

        Assert.Contains(staticItems, item => item.Label == "named");
    }

    [Fact]
    public void Signature_help_includes_constructors_static_calls_and_instance_methods()
    {
        var (constructorText, constructorPosition) = ExtractCursor("""
            using System.Drawing
            var pt = new Point(¦)
            """);
        var (staticText, staticPosition) = ExtractCursor("""
            var joined = String.Join(", ", ¦)
            """);
        var (instanceText, instancePosition) = ExtractCursor("""
            var ok = "toast".Contains(¦)
            """);

        var constructorHelp = _features.GetSignatureHelp(constructorText, "file:///signature-help-ctor.tosh", constructorPosition);
        var staticHelp = _features.GetSignatureHelp(staticText, "file:///signature-help-static.tosh", staticPosition);
        var instanceHelp = _features.GetSignatureHelp(instanceText, "file:///signature-help-instance.tosh", instancePosition);

        Assert.NotNull(constructorHelp);
        Assert.Contains(constructorHelp!.Signatures, signature => signature.Label.Contains("System.Drawing.Point(", StringComparison.Ordinal));
        Assert.Equal(0, constructorHelp.ActiveParameter);

        Assert.NotNull(staticHelp);
        Assert.Contains(staticHelp!.Signatures, signature => signature.Label.Contains("Join(", StringComparison.Ordinal));
        Assert.Equal(1, staticHelp.ActiveParameter);

        Assert.NotNull(instanceHelp);
        Assert.Contains(instanceHelp!.Signatures, signature => signature.Label.Contains("Contains(", StringComparison.Ordinal));
        Assert.Equal(0, instanceHelp.ActiveParameter);
    }

    [Fact]
    public void Signature_help_includes_tosh_class_constructors_and_methods()
    {
        var (constructorText, constructorPosition) = ExtractCursor("""
            class Item {
                Item() { }

                Item(name: string) {
                }

                static func named(name: string) -> Item {
                    return new Item($name)
                }

                func rename(name: string, category: string?) {
                }
            }

            var item = new Item()
            var created = new Item(¦)
            """);
        var (staticText, staticPosition) = ExtractCursor("""
            class Item {
                static func named(name: string) -> Item {
                    return new Item($name)
                }
            }

            var created = Item.named(¦)
            """);
        var (instanceText, instancePosition) = ExtractCursor("""
            class Item {
                func rename(name: string, category: string?) {
                }
            }

            var item = new Item()
            var renamed = $item.rename(¦)
            """);

        var constructorHelp = _features.GetSignatureHelp(constructorText, "file:///tosh-class-ctor.tosh", constructorPosition);
        var staticHelp = _features.GetSignatureHelp(staticText, "file:///tosh-class-static.tosh", staticPosition);
        var instanceHelp = _features.GetSignatureHelp(instanceText, "file:///tosh-class-instance.tosh", instancePosition);

        Assert.NotNull(constructorHelp);
        Assert.Contains(constructorHelp!.Signatures, signature => signature.Label == "Item()");
        Assert.Contains(constructorHelp.Signatures, signature => signature.Label == "Item(name: string)");

        Assert.NotNull(staticHelp);
        Assert.Contains(staticHelp!.Signatures, signature => signature.Label.Contains("named(name: string)", StringComparison.Ordinal));

        Assert.NotNull(instanceHelp);
        Assert.Contains(instanceHelp!.Signatures, signature => signature.Label.Contains("rename(name: string, category: string?)", StringComparison.Ordinal));
    }

    [Fact]
    public void Signature_help_includes_top_level_function_overloads_for_command_calls()
    {
        var (text, position) = ExtractCursor("""
            func greet() { echo noargs }
            func greet(name: string) { echo hello $name }
            func greet(name: string, title: string?) { echo hello $title $name }

            greet toast s¦ir
            """);

        var help = _features.GetSignatureHelp(text, "file:///tosh-function-overloads.tosh", position);

        Assert.NotNull(help);
        Assert.Contains(help!.Signatures, signature => signature.Label == "func greet()");
        Assert.Contains(help.Signatures, signature => signature.Label == "func greet(name: string)");
        Assert.Contains(help.Signatures, signature => signature.Label == "func greet(name: string, title: string?)");
        Assert.Equal(1, help.ActiveParameter);
    }

    [Fact]
    public void Require_can_feed_completion_and_signature_help_for_fixture_types()
    {
        _ = FixtureAssemblyPath.Value;

        var (completionText, completionPosition) = ExtractCursor($$"""
            require "{{FixtureProjectPath}}"
            using Tosh.LspFixture
            var widget = new Widget("demo", 3)
            $widget.¦
            """);
        var (constructorText, constructorPosition) = ExtractCursor($$"""
            require "{{FixtureProjectPath}}"
            using Tosh.LspFixture
            var widget = new Widget(¦)
            """);
        var (staticText, staticPosition) = ExtractCursor($$"""
            require "{{FixtureProjectPath}}"
            using Tosh.LspFixture
            var widget = Widget.Create(¦)
            """);

        var completionItems = _features.GetCompletionItems(completionText, completionPosition, FixtureDocumentUri);
        var constructorHelp = _features.GetSignatureHelp(constructorText, FixtureDocumentUri, constructorPosition);
        var staticHelp = _features.GetSignatureHelp(staticText, FixtureDocumentUri, staticPosition);

        Assert.Contains(completionItems, item => item.Label == "Name");
        Assert.Contains(completionItems, item => item.Label == "Rename");

        Assert.NotNull(constructorHelp);
        Assert.Contains(constructorHelp!.Signatures, signature => signature.Label.Contains("Tosh.LspFixture.Widget(", StringComparison.Ordinal));

        Assert.NotNull(staticHelp);
        Assert.Contains(staticHelp!.Signatures, signature => signature.Label.Contains("Create(", StringComparison.Ordinal));
    }

    [Fact]
    public void Hover_describes_keywords_and_special_variables()
    {
        var keywordHover = _features.GetHover("func greet() { }", "file:///hover-keyword.tosh", new LspPosition(0, 1));
        var resultHover = _features.GetHover("$tosh", "file:///hover-result.tosh", new LspPosition(0, 2));

        Assert.NotNull(keywordHover);
        Assert.Contains("Define a function", keywordHover!.Contents.Value, StringComparison.Ordinal);

        Assert.NotNull(resultHover);
        Assert.Contains("runtime namespace", resultHover!.Contents.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hover_describes_clr_types_and_members()
    {
        var (typeText, typePosition) = ExtractCursor("""
            using System.Drawing
            var pt = new Poin¦t(2, 2)
            """);
        var (memberText, memberPosition) = ExtractCursor("""
            using System.Drawing
            var pt = new Point(2, 2)
            $pt.¦X
            """);
        var (methodText, methodPosition) = ExtractCursor("""
            func check(text: string) {
                $text.¦Contains("x")
            }
            """);

        var typeHover = _features.GetHover(typeText, "file:///hover-type.tosh", typePosition);
        var memberHover = _features.GetHover(memberText, "file:///hover-member.tosh", memberPosition);
        var methodHover = _features.GetHover(methodText, "file:///hover-method.tosh", methodPosition);

        Assert.NotNull(typeHover);
        Assert.Contains("System.Drawing.Point", typeHover!.Contents.Value, StringComparison.Ordinal);

        Assert.NotNull(memberHover);
        Assert.Contains("System.Int32 X", memberHover!.Contents.Value, StringComparison.Ordinal);

        Assert.NotNull(methodHover);
        Assert.Contains("Contains(", methodHover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_describes_tosh_classes_variables_properties_and_methods()
    {
        var (classText, classPosition) = ExtractCursor("""
            class Ite¦m {
                prop Name: string? = null

                static func named(name: string) -> Item {
                    return new Item()
                }
            }
            """);
        var (variableText, variablePosition) = ExtractCursor("""
            class Item {
                prop Name: string? = null
            }

            var item = new Item()
            $ite¦m
            """);
        var (propertyText, propertyPosition) = ExtractCursor("""
            class Item {
                prop Name: string? = null
            }

            var item = new Item()
            $item.¦Name
            """);
        var (methodText, methodPosition) = ExtractCursor("""
            class Item {
                static func named(name: string) -> Item {
                    return new Item()
                }
            }

            Item.¦named("toast")
            """);

        var classHover = _features.GetHover(classText, "file:///hover-shell-class.tosh", classPosition);
        var variableHover = _features.GetHover(variableText, "file:///hover-shell-variable.tosh", variablePosition);
        var propertyHover = _features.GetHover(propertyText, "file:///hover-shell-property.tosh", propertyPosition);
        var methodHover = _features.GetHover(methodText, "file:///hover-shell-method.tosh", methodPosition);

        Assert.NotNull(classHover);
        Assert.Contains("Class", classHover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Item", classHover.Contents.Value, StringComparison.Ordinal);

        Assert.NotNull(variableHover);
        Assert.Contains("Variable", variableHover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Item $item", variableHover.Contents.Value, StringComparison.Ordinal);

        Assert.NotNull(propertyHover);
        Assert.Contains("string? Name", propertyHover!.Contents.Value, StringComparison.Ordinal);

        Assert.NotNull(methodHover);
        Assert.Contains("named(name: string)", methodHover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_describes_top_level_function_overloads()
    {
        var (text, position) = ExtractCursor("""
            func greet() { echo noargs }
            func greet(name: string) { echo hello $name }

            gre¦et toast
            """);

        var hover = _features.GetHover(text, "file:///hover-function-overloads.tosh", position);

        Assert.NotNull(hover);
        Assert.Contains("Functions", hover!.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("func greet()", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("func greet(name: string)", hover.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_symbols_include_functions_variables_and_classes()
    {
        const string script = """
            var person
            class Item { }
            func ll => ls -la
            func greet(name) {
                echo $name
            }
            """;

        var symbols = _features.GetDocumentSymbols(script, "test.tosh");

        Assert.Contains(symbols, symbol => symbol.Name == "person");
        Assert.Contains(symbols, symbol => symbol.Name == "Item");
        Assert.Contains(symbols, symbol => symbol.Name == "ll");
        Assert.Contains(symbols, symbol => symbol.Name == "greet");
    }

    [Fact]
    public void Document_symbols_group_same_scope_function_overloads()
    {
        const string script = """
            func greet() { echo noargs }
            func greet(name: string) { echo hello $name }
            """;

        var symbols = _features.GetDocumentSymbols(script, "test.tosh");
        var greetSymbols = symbols.Where(symbol => symbol.Name == "greet").ToArray();

        var greet = Assert.Single(greetSymbols);
        Assert.Equal("func (2 overloads)", greet.Detail);
    }

    [Fact]
    public void Flat_symbol_information_can_be_produced_for_non_hierarchical_clients()
    {
        const string script = """
            var person
            class Item { }
            func greet(name) {
                echo $name
            }
            """;

        var symbols = _features.GetSymbolInformations(script, "file:///test.tosh");

        Assert.Contains(symbols, symbol => symbol.Name == "person" && symbol.Location.Uri == "file:///test.tosh");
        Assert.Contains(symbols, symbol => symbol.Name == "Item" && symbol.Location.Uri == "file:///test.tosh");
        Assert.Contains(symbols, symbol => symbol.Name == "greet" && symbol.Location.Uri == "file:///test.tosh");
    }

    [Fact]
    public void Definitions_resolve_variable_function_and_class_targets()
    {
        const string script = "class Item { }\nfunc test1(a, b, c) {\n  echo String.Join(\":\", [$a, $b, $c])\n}\nfunc t1 => test1 $1 \"Jim\" $2\nvar item = new Item()\nt1 one two\n";

        var functionDefinitions = _features.GetDefinitions(script, "file:///test.tosh", new LspPosition(6, 1));
        var variableDefinitions = _features.GetDefinitions(script, "file:///test.tosh", new LspPosition(2, 30));
        var classDefinitions = _features.GetDefinitions(script, "file:///test.tosh", new LspPosition(5, 17));

        var functionDefinition = Assert.Single(functionDefinitions);
        Assert.Equal("file:///test.tosh", functionDefinition.Uri);
        Assert.Equal(4, functionDefinition.Range.Start.Line);
        Assert.Equal(5, functionDefinition.Range.Start.Character);

        var variableDefinition = Assert.Single(variableDefinitions);
        Assert.Equal("file:///test.tosh", variableDefinition.Uri);
        Assert.Equal(1, variableDefinition.Range.Start.Line);

        var classDefinition = Assert.Single(classDefinitions);
        Assert.Equal("file:///test.tosh", classDefinition.Uri);
        Assert.Equal(0, classDefinition.Range.Start.Line);
    }

    [Fact]
    public void Definitions_return_all_visible_function_overloads()
    {
        const string script = """
            func greet() { echo noargs }
            func greet(name: string) { echo hello $name }
            greet toast
            """;

        var definitions = _features.GetDefinitions(script, "file:///defs-overloads.tosh", new LspPosition(2, 2));

        Assert.Equal(2, definitions.Count);
        Assert.Contains(definitions, definition => definition.Range.Start.Line == 0);
        Assert.Contains(definitions, definition => definition.Range.Start.Line == 1);
    }

    [Fact]
    public void Hover_shows_rich_markdown_for_builtin_commands()
    {
        var (text, position) = ExtractCursor("sor¦t -r");

        var hover = _features.GetHover(text, "file:///hover-builtin.tosh", position);

        Assert.NotNull(hover);
        var md = hover!.Contents.Value;
        Assert.Contains("sort", md, StringComparison.Ordinal);
        // Should include usage code block
        Assert.Contains("```tosh", md, StringComparison.Ordinal);
        // Should include the arguments section
        Assert.Contains("**Arguments**", md, StringComparison.Ordinal);
        Assert.Contains("key", md, StringComparison.Ordinal);
        // Should include the options section
        Assert.Contains("**Options**", md, StringComparison.Ordinal);
        Assert.Contains("-r", md, StringComparison.Ordinal);
        // Should include examples
        Assert.Contains("**Examples**", md, StringComparison.Ordinal);
        // Should include pipeline input info
        Assert.Contains("**Pipeline input:**", md, StringComparison.Ordinal);
        // Should include output info
        Assert.Contains("**Output:**", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Completion_items_include_flags_for_builtin_commands()
    {
        var items = _features.GetCompletionItems("sort -", new LspPosition(0, 6));

        Assert.Contains(items, item => item.Label == "-r");
        Assert.Contains(items, item => item.Label == "-n");
        Assert.Contains(items, item => item.Label == "-u");
        Assert.Contains(items, item => item.Label == "-h");
        Assert.All(items, item => Assert.Equal(20, item.Kind));
    }

    [Fact]
    public void Completion_items_filter_flags_by_prefix()
    {
        var items = _features.GetCompletionItems("tree --sh", new LspPosition(0, 9));

        Assert.Contains(items, item => item.Label == "--show <columns>");
        Assert.Contains(items, item => item.Label == "--show-all");
        Assert.DoesNotContain(items, item => item.Label == "--hide <columns>");
    }

    [Fact]
    public void Completion_items_include_flags_after_pipeline()
    {
        var items = _features.GetCompletionItems("ls | sort -", new LspPosition(0, 11));

        Assert.Contains(items, item => item.Label == "-r");
        Assert.Contains(items, item => item.Label == "-n");
    }

    [Fact]
    public void Signature_help_shows_builtin_command_arguments()
    {
        var (text, position) = ExtractCursor("echo hell¦o");

        var help = _features.GetSignatureHelp(text, "file:///sig-builtin.tosh", position);

        Assert.NotNull(help);
        Assert.Single(help!.Signatures);
        var signature = help.Signatures[0];
        Assert.Contains("echo", signature.Label, StringComparison.Ordinal);
        Assert.Contains("value", signature.Label, StringComparison.Ordinal);
        Assert.Equal("Emits its arguments as pipeline objects.", signature.Documentation);
        Assert.NotEmpty(signature.Parameters!);
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void Signature_help_shows_optional_argument_with_type()
    {
        var (text, position) = ExtractCursor("sort ¦key");

        var help = _features.GetSignatureHelp(text, "file:///sig-sort.tosh", position);

        Assert.NotNull(help);
        var signature = Assert.Single(help!.Signatures);
        Assert.Contains("key?", signature.Label, StringComparison.Ordinal);
        Assert.Contains("member-path|callable|block", signature.Label, StringComparison.Ordinal);
    }

    private static (string Text, LspPosition Position) ExtractCursor(string textWithCursor)
    {
        var cursorIndex = textWithCursor.IndexOf('¦');
        Assert.True(cursorIndex >= 0, "The test text must contain a cursor marker.");

        var line = 0;
        var character = 0;

        for (var index = 0; index < cursorIndex; index++)
        {
            if (textWithCursor[index] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return (textWithCursor.Remove(cursorIndex, 1), new LspPosition(line, character));
    }

    private static string EnsureFixtureAssemblyBuilt()
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "build", FixtureProjectPath, "/m:1" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        Assert.NotNull(process);
        process!.WaitForExit();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        Assert.True(process.ExitCode == 0, $"Failed to build LSP fixture project.\nSTDOUT:\n{output}\nSTDERR:\n{error}");

        var assemblyPath = Path.Combine(Path.GetDirectoryName(FixtureProjectPath)!, "bin", "Debug", "net10.0", "Tosh.LspFixture.dll");
        Assert.True(File.Exists(assemblyPath), $"Expected the built fixture assembly to exist at '{assemblyPath}'.");
        return assemblyPath;
    }
}

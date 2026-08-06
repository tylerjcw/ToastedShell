using Tosh.LanguageServices;
using Xunit;

namespace Tosh.Tests;

public sealed class DocumentSymbolHierarchyTests
{
    private readonly ToshLanguageFeatures _features = new();

    [Fact]
    public void Document_symbols_include_hierarchical_children_for_modules_classes_and_records()
    {
        const string script = """
            module Geometry {
                func area(r) { return 3.14 * $r * $r }
            }

            class Point(x, y) {
                prop X = $x
                func distance(other) { return 0 }
            }

            record Vec2(x, y)

            enum Color { Red, Green, Blue }

            subcommand build {
                flag target: string = "all"
            }
            """;

        var symbols = _features.GetDocumentSymbols(script, "test.tosh");

        var moduleSymbol = Assert.Single(symbols, s => s.Name == "Geometry");
        Assert.Equal(2, moduleSymbol.Kind); // Module
        Assert.NotNull(moduleSymbol.Children);
        var areaFunc = Assert.Single(moduleSymbol.Children);
        Assert.Equal("area", areaFunc.Name);

        var classSymbol = Assert.Single(symbols, s => s.Name == "Point");
        Assert.Equal(5, classSymbol.Kind); // Class
        Assert.NotNull(classSymbol.Children);
        Assert.Contains(classSymbol.Children, child => child.Name == "X" && child.Kind == 7); // Property
        Assert.Contains(classSymbol.Children, child => child.Name == "distance" && child.Kind == 6); // Method

        var recordSymbol = Assert.Single(symbols, s => s.Name == "Vec2");
        Assert.Equal(23, recordSymbol.Kind); // Struct
        Assert.NotNull(recordSymbol.Children);
        Assert.Contains(recordSymbol.Children, child => child.Name == "x" && child.Kind == 8); // RecordField
        Assert.Contains(recordSymbol.Children, child => child.Name == "y" && child.Kind == 8); // RecordField

        var enumSymbol = Assert.Single(symbols, s => s.Name == "Color");
        Assert.Equal(10, enumSymbol.Kind); // Enum
        Assert.NotNull(enumSymbol.Children);
        Assert.Equal(3, enumSymbol.Children.Count);
        Assert.Contains(enumSymbol.Children, child => child.Name == "Red" && child.Kind == 22); // EnumMember

        var subcommandSymbol = Assert.Single(symbols, s => s.Name == "build");
        Assert.Equal(12, subcommandSymbol.Kind); // Function / Subcommand
        Assert.NotNull(subcommandSymbol.Children);
        Assert.Contains(subcommandSymbol.Children, child => child.Name == "target" && child.Kind == 7); // Flag/Property
    }

    [Fact]
    public void Document_symbols_properly_nest_modules_and_exclude_local_variables()
    {
        const string script = """
            require ToastLib.Shell from "/path/to/Shell.tosh"

            partial module ToastLib {
                partial module Filesystem {
                    export func GetFolder(path) {
                        var parentDir = "test"
                        return $parentDir
                    }
                }
            }
            """;

        var symbols = _features.GetDocumentSymbols(script, "test.tosh");

        // ToastLib should appear ONCE as a top-level module
        var toastLib = Assert.Single(symbols, s => s.Name == "ToastLib");
        Assert.Equal(2, toastLib.Kind); // Module
        Assert.NotNull(toastLib.Children);

        // Filesystem should be nested under ToastLib
        var filesystem = Assert.Single(toastLib.Children, c => c.Name == "Filesystem");
        Assert.Equal(2, filesystem.Kind); // Module
        Assert.NotNull(filesystem.Children);

        // GetFolder should be nested under Filesystem
        var getFolder = Assert.Single(filesystem.Children, c => c.Name == "GetFolder");
        Assert.Equal(12, getFolder.Kind); // Function

        // Local variable 'parentDir' inside GetFolder body MUST NOT appear in document outline
        Assert.DoesNotContain(symbols, s => s.Name == "parentDir");
        if (getFolder.Children != null)
        {
            Assert.DoesNotContain(getFolder.Children, c => c.Name == "parentDir");
        }
    }
}

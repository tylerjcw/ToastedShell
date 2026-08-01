using Tosh.LanguageServices;

namespace Tosh.Tests;

/// <summary>
/// Names from a <c>require</c>d file reach the editor — <c>TS-P3-12</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>DeclarationIndex</c> was document-local, so a file built on
/// `require "./lib/Point.tosh"` got no completion, no hover and no semantic-token colouring for
/// anything that library declared. Every name from it read as undeclared, which is why a profile
/// full of `ToastLib.*` types rendered as plain text while the same names coloured correctly in
/// the REPL — the REPL's highlighter consults the live runtime, so it never had this gap.
/// </para>
/// <para>
/// The tests use real files on disk because that is the whole mechanism: resolving a relative
/// target against the requiring document's directory. A fixture that stubbed the file system
/// would test everything except the part that was missing.
/// </para>
/// </remarks>
public sealed class RequiredTypeVisibilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tosh-require-index-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>An index plus the source it came from, so offsets can be computed by line.</summary>
    private sealed record Indexed(DeclarationIndex Index, string Source)
    {
        /// <summary>Offset of the start of <paramref name="line"/> (0-based).</summary>
        public int At(int line)
        {
            var offset = 0;

            for (var current = 0; current < line; current++)
            {
                var next = Source.IndexOf('\n', offset);
                if (next < 0) return Source.Length;
                offset = next + 1;
            }

            return offset;
        }
    }

    /// <summary>Indexes <paramref name="source"/> as a file in the temp root.</summary>
    private Indexed IndexOf(string source)
    {
        var path = Write("main.tosh", source);
        return new Indexed(DeclarationIndex.Create(path, source), source);
    }

    [Fact]
    public void A_required_files_class_is_visible()
    {
        Write("lib/Shapes.tosh", "export class Circle { prop Radius = 0 }\n");

        var index = IndexOf("require \"./lib/Shapes.tosh\"\nvar c = (new Circle())\n");

        Assert.Contains("Circle", index.Index.GetVisibleTypeLikeSymbols(index.At(1)));
    }

    [Theory]
    // Every top-level shape a library publishes, since completion and colouring treat them alike.
    [InlineData("export class Widget { }", "Widget")]
    [InlineData("export record Point(X: int, Y: int)", "Point")]
    [InlineData("export enum Colour { Red, Green }", "Colour")]
    public void A_required_type_like_declaration_is_visible(string declaration, string name)
    {
        Write("lib/Kinds.tosh", declaration + "\n");

        var index = IndexOf("require \"./lib/Kinds.tosh\"\necho hi\n");

        Assert.Contains(name, index.Index.GetVisibleTypeLikeSymbols(index.At(1)));
    }

    [Fact]
    public void A_required_files_function_is_visible()
    {
        Write("lib/Util.tosh", "export func greet(name) { return $name }\n");

        var index = IndexOf("require \"./lib/Util.tosh\"\ngreet \"world\"\n");

        Assert.Contains("greet", index.Index.GetVisibleFunctions(index.At(1)));
    }

    [Fact]
    public void A_module_and_the_types_inside_it_are_both_visible()
    {
        // The reporter's own shape: `partial module ToastLib { partial module System { … } }`.
        Write("lib/Lib.tosh", """
            partial module ToastLib {
                partial module Types {
                    export class Info { prop Name = "x" }
                }
            }
            """);

        var index = IndexOf("require \"./lib/Lib.tosh\"\necho hi\n");
        var offset = index.At(1);

        Assert.Contains("ToastLib", index.Index.GetVisibleModules(offset));
        Assert.Contains("Info", index.Index.GetVisibleTypeLikeSymbols(offset));
    }

    [Fact]
    public void An_imported_name_is_not_visible_above_the_require()
    {
        // Imported declarations take the requiring statement's position, so "declared before use"
        // still means something. Otherwise a name would appear to exist on line 1 of a file that
        // requires it on line 40.
        Write("lib/Late.tosh", "export class Late { }\n");

        var index = IndexOf("echo first\nrequire \"./lib/Late.tosh\"\necho second\n");

        Assert.DoesNotContain("Late", index.Index.GetVisibleTypeLikeSymbols(index.At(0)));
        Assert.Contains("Late", index.Index.GetVisibleTypeLikeSymbols(index.At(2)));
    }

    [Fact]
    public void A_require_chain_is_followed()
    {
        Write("lib/Inner.tosh", "export class Inner { }\n");
        Write("lib/Outer.tosh", "require \"./Inner.tosh\"\nexport class Outer { }\n");

        var index = IndexOf("require \"./lib/Outer.tosh\"\necho hi\n");
        var visible = index.Index.GetVisibleTypeLikeSymbols(index.At(1));

        Assert.Contains("Outer", visible);
        Assert.Contains("Inner", visible);
    }

    [Fact]
    public void A_require_cycle_terminates()
    {
        // Two libraries requiring each other is a mistake, but an editor feature that hangs or
        // stack-overflows on a mistake is a worse one. The visited set is what makes this finish.
        Write("lib/A.tosh", "require \"./B.tosh\"\nexport class Alpha { }\n");
        Write("lib/B.tosh", "require \"./A.tosh\"\nexport class Beta { }\n");

        var index = IndexOf("require \"./lib/A.tosh\"\necho hi\n");
        var visible = index.Index.GetVisibleTypeLikeSymbols(index.At(1));

        Assert.Contains("Alpha", visible);
        Assert.Contains("Beta", visible);
    }

    [Theory]
    // A file being edited is usually broken in one of these ways, and none of them may throw.
    [InlineData("require \"./lib/does-not-exist.tosh\"\necho hi\n")]
    [InlineData("require \"./lib/\"\necho hi\n")]
    [InlineData("require \"\"\necho hi\n")]
    [InlineData("require ToastLib.Math\necho hi\n")]
    public void An_unresolvable_require_is_skipped_quietly(string source)
    {
        var index = IndexOf(source);

        // The point is that indexing completed at all; the document's own names survive.
        Assert.NotNull(index.Index.GetVisibleTypeLikeSymbols(index.At(1)));
    }

    [Fact]
    public void A_required_file_that_does_not_parse_does_not_lose_local_names()
    {
        Write("lib/Broken.tosh", "class { { { unterminated\n");

        var index = IndexOf("require \"./lib/Broken.tosh\"\nclass Local { }\necho hi\n");

        Assert.Contains("Local", index.Index.GetVisibleTypeLikeSymbols(index.At(2)));
    }

    [Fact]
    public void A_document_named_by_uri_resolves_its_requires()
    {
        // The language server identifies documents by URI, not by path. Every other test here
        // passed while the editor saw nothing, because `Path.GetDirectoryName("file:///…")`
        // yields `file:/…` and resolves to nothing — the unit tests were the only caller passing
        // a plain path. Caught by driving the real server over stdio, and pinned here so the URI
        // form is covered without needing a subprocess.
        Write("lib/Uri.tosh", "export class FromUri { }\n");
        var path = Write("main.tosh", "require \"./lib/Uri.tosh\"\necho hi\n");
        var source = File.ReadAllText(path);

        var index = DeclarationIndex.Create(new Uri(path).AbsoluteUri, source);

        Assert.Contains("FromUri", index.GetVisibleTypeLikeSymbols(source.IndexOf("echo", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_deep_require_chain_stops_at_the_depth_bound()
    {
        // The bound exists because this runs on every keystroke. Four levels deep is past it, so
        // the fourth library's names are absent while the first three are present — asserted so
        // that raising or lowering MaxRequireDepth is a deliberate change with a visible effect.
        Write("lib/L4.tosh", "export class Level4 { }\n");
        Write("lib/L3.tosh", "require \"./L4.tosh\"\nexport class Level3 { }\n");
        Write("lib/L2.tosh", "require \"./L3.tosh\"\nexport class Level2 { }\n");
        Write("lib/L1.tosh", "require \"./L2.tosh\"\nexport class Level1 { }\n");

        var index = IndexOf("require \"./lib/L1.tosh\"\necho hi\n");
        var visible = index.Index.GetVisibleTypeLikeSymbols(index.At(1));

        Assert.Contains("Level1", visible);
        Assert.Contains("Level2", visible);
        Assert.Contains("Level3", visible);
        Assert.DoesNotContain("Level4", visible);
    }
}

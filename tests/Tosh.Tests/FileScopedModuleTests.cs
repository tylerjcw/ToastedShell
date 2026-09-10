using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A module declared without a body takes the rest of the file — <c>TOAST-0120</c>.
/// </summary>
/// <remarks>
/// <para>
/// The dotted <em>block</em> form already worked: `module A.B.C { … }` compiles to nested
/// modules, and several files can contribute to the same path. What every file in a library
/// paid for was the wrapping — two or three `partial module` lines and their closing braces
/// around every declaration, and the indentation that comes with them.
/// </para>
/// <para>
/// The body is the rest of the file, so it does not exist when the header is read. The header
/// leaves a placeholder in the statement list and the top-level loop closes the module over
/// everything after it once the file is parsed. Statements written *above* the line stay at
/// the top level, which is what makes `using` and `require` usable before it.
/// </para>
/// </remarks>
public sealed class FileScopedModuleTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => $"{value}"));
    }

    /// <summary>
    /// Loads <paramref name="library"/>, then runs <paramref name="caller"/> against it.
    /// </summary>
    /// <remarks>
    /// Two sources rather than one, because the form takes the *rest of the file*: a call
    /// written below the declaration would be inside the module rather than reaching it
    /// from outside. That is the shape it has in use, where a library file declares and a
    /// script calls.
    /// </remarks>
    private static async Task<string> RunAgainstAsync(string library, string caller)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(library);
        var results = await engine.ExecuteToListAsync(caller);
        return string.Join(",", results.Select(value => $"{value}"));
    }

    [Theory]
    [InlineData("module Shapes\nexport func Name() -> string => \"flat\"", "Shapes.Name()", "flat")]
    [InlineData("module A.B.C\nexport func Deep() -> string => \"deep\"", "A.B.C.Deep()", "deep")]
    [InlineData("partial module A.B\nexport func P() -> string => \"partial\"", "A.B.P()", "partial")]
    [InlineData("export module A.B\nexport func E() -> string => \"export\"", "A.B.E()", "export")]
    public async Task A_module_without_a_body_takes_the_rest_of_the_file(
        string library,
        string caller,
        string expected)
    {
        Assert.Equal(expected, await RunAgainstAsync(library, caller));
    }

    [Fact]
    public async Task Everything_after_the_line_is_inside_it()
    {
        // Classes and nested block modules alike, and the nesting composes: a block module
        // written below the line lands underneath it rather than beside it.
        var library =
            "module A.B\n"
            + "export class Thing(v) { prop V = $v }\n"
            + "module Inner {\n"
            + "    export func Deep() -> string { return \"inner\" }\n"
            + "}";

        Assert.Equal("7 inner", await RunAgainstAsync(
            library,
            "echo $\"{(new A.B.Thing(7)).V} {A.B.Inner.Deep()}\""));
    }

    [Fact]
    public async Task Statements_above_the_line_stay_at_the_top_level()
    {
        // The reason the form is usable at all: `require` and `using` come first.
        var library =
            "var outside = \"top level\"\n"
            + "module A\n"
            + "export func Inside() -> string { return \"inside\" }";

        Assert.Equal("top level inside", await RunAgainstAsync(
            library,
            "echo $\"{$outside} {A.Inside()}\""));
    }

    [Fact]
    public async Task Two_files_contribute_to_one_partial_module()
    {
        // The dotted wrappers are partial, so siblings under the same parent do not collide.
        var source =
            "partial module A.B { export func One() -> string { return \"1\" } }\n"
            + "partial module A.C { export func Two() -> string { return \"2\" } }\n"
            + "echo $\"{A.B.One()}{A.C.Two()}\"";

        Assert.Equal("12", await RunAsync(source));
    }

    [Fact]
    public async Task A_second_bodyless_module_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(
            () => RunAsync("module A\nexport func One() { return 1 }\nmodule B\nexport func Two() { return 2 }"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.parser.file_scoped_module_repeated");
    }

    [Fact]
    public async Task A_bodyless_module_inside_a_block_is_refused()
    {
        // "the rest of the file" has no meaning inside a brace, so it is named rather than
        // silently taken to mean "the rest of the block".
        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(
            () => RunAsync("func f() {\n    module Oops\n    return 1\n}\nf()"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.parser.file_scoped_module_nested");
    }

    [Theory]
    // The block form is untouched, and so is every other use of the word.
    [InlineData("module A { export func F() -> string { return \"block\" } }\nA.F()", "block")]
    [InlineData("module A.B.C { export func F() -> string { return \"dotted\" } }\nA.B.C.F()", "dotted")]
    [InlineData("var module = 5\n$module", "5")]
    public async Task Forms_that_already_worked_are_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }
}

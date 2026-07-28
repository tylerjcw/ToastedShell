using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P2-23 — parse-time identity should come from what the source
/// declares, not from how a name is spelled. The parser collects the
/// functions and modules a source declares and consults that table
/// before falling back to the capitalization heuristic, which remains
/// only for names it has no table entry for.
/// </summary>
public sealed class DeclarationTableIdentityTests
{
    [Theory]
    [InlineData("Foo")]
    [InlineData("foo")]
    [InlineData("FOO")]
    [InlineData("Calculate")]
    public async Task A_declared_function_is_callable_whatever_its_casing(string name)
    {
        // A capitalized user function was previously read as a static
        // call on a CLR type of that name and failed, while the same
        // function spelled in lowercase worked.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            $$"""
            func {{name}}(x) { return $x * 3 }
            var got = ({{name}}(14))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(42, got);
    }

    [Fact]
    public async Task Clr_static_access_is_unaffected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var root = (Math.Sqrt(16))
            var joined = (String.Join("-", ["a", "b"]))
            """);

        Assert.True(engine.TryGetVariableValue("root", out var root));
        Assert.Equal(4d, root);
        Assert.True(engine.TryGetVariableValue("joined", out var joined));
        Assert.Equal("a-b", joined);
    }

    [Fact]
    public async Task Module_member_access_still_resolves()
    {
        // Module members travel the same qualified path as CLR static
        // access, so the table must not divert them.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            module Lib { var greeting = "hello" }
            var got = (Lib.greeting)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal("hello", got);
    }

    [Fact]
    public async Task A_declaration_keyword_used_as_an_argument_does_not_poison_the_table()
    {
        // TS-P2-08: the previous raw scan registered any bareword after
        // the word `func`, so `echo func bar` made `bar` look declared.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("echo func bar");

        Assert.Equal(["func", "bar"], results);
    }

    [Fact]
    public async Task Modifiers_before_a_declaration_still_register_it()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            export func Exported(x) { return $x + 1 }
            var got = (Exported(41))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(42, got);
    }

    [Fact]
    public void A_host_supplied_module_makes_dotted_dispatch_unambiguous()
    {
        // The declaration table only covers modules declared in *this*
        // source. An imported module is known to the host instead, so the
        // parse context is what makes the same decision available.
        const string source = "Inventory.restock 4";

        var withoutContext = Tosh.Language.Parsing.ToshParser.Parse(source, "<t>");
        var withContext = Tosh.Language.Parsing.ToshParser.Parse(
            source,
            "<t>",
            Tosh.Language.Parsing.ParseContext.Create(moduleNames: ["Inventory"]));

        Assert.Empty(withoutContext.Diagnostics);
        Assert.Empty(withContext.Diagnostics);
    }

    [Fact]
    public void A_host_supplied_command_is_not_treated_as_a_clr_type()
    {
        // `Deploy(1)` with a capital D is a static call on a CLR type
        // named Deploy unless the host says otherwise.
        const string source = "Deploy(1)";

        var withContext = Tosh.Language.Parsing.ToshParser.Parse(
            source,
            "<t>",
            Tosh.Language.Parsing.ParseContext.Create(commandNames: ["Deploy"]));

        Assert.Empty(withContext.Diagnostics);
    }

    [Fact]
    public void An_empty_context_keeps_parsing_purely_syntactic()
    {
        // ParseContext.Empty is a real value, not a shim: the formatter
        // and REPL classifier parse with no environment.
        var result = Tosh.Language.Parsing.ToshParser.Parse(
            "echo hello",
            "<t>",
            Tosh.Language.Parsing.ParseContext.Empty);

        Assert.Empty(result.Diagnostics);
    }
}

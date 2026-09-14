using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A loop variable that says what it holds: <c>for (int n) in $lines { … }</c>.
/// </summary>
/// <remarks>
/// The annotation means what it means on a declaration — <c>var n: int = …</c>, once per
/// turn of the loop — rather than being a comment the runtime never reads.
/// </remarks>
public sealed class TypedLoopVariableTests
{
    private static ToshEngine Engine() => new(ToshRuntime.CreateDefault().Language);

    [Theory]
    // The declaration alone is parenthesised …
    [InlineData("for (i) in 1..3 { echo $i }", "i", null, false)]
    [InlineData("for (var i) in 1..3 { echo $i }", "i", null, true)]
    [InlineData("for (int i) in 1..3 { echo $i }", "i", "int", false)]
    [InlineData("for (i: int) in 1..3 { echo $i }", "i", "int", false)]
    [InlineData("for (var i: int) in 1..3 { echo $i }", "i", "int", true)]
    // … or the whole clause is, which is what most people try first.
    [InlineData("for (i in 1..3) { echo $i }", "i", null, false)]
    [InlineData("for (var i in 1..3) { echo $i }", "i", null, true)]
    [InlineData("for (int i in 1..3) { echo $i }", "i", "int", false)]
    [InlineData("for (i: int in 1..3) { echo $i }", "i", "int", false)]
    [InlineData("for (var i: int in 1..3) { echo $i }", "i", "int", true)]
    // … or nothing is, as it always was.
    [InlineData("for i in 1..3 { echo $i }", "i", null, false)]
    public void A_loop_variable_may_be_declared_every_way_that_reads_naturally(
        string source, string name, string? type, bool usesVar)
    {
        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<ForStatementSyntax>(result.Statement);

        Assert.Equal(name, statement.VariableName);
        Assert.Equal(type, statement.TypeName);
        Assert.Equal(usesVar, statement.UsesVar);
    }

    [Fact]
    public void Which_bracket_closes_the_clause_is_decided_by_what_follows_the_name()
    {
        // Neither spelling is ambiguous, because the token after the declaration says which
        // one was written: `)` closes a declaration, `in` continues a clause.
        var declaration = Assert.IsType<ForStatementSyntax>(
            ToshParser.Parse("for (var i) in 1..3 { echo $i }").Statement);
        var clause = Assert.IsType<ForStatementSyntax>(
            ToshParser.Parse("for (var i in 1..3) { echo $i }").Statement);

        Assert.Equal(declaration.Source.Stages.Count, clause.Source.Stages.Count);
        Assert.Equal(declaration.VariableName, clause.VariableName);
    }

    [Fact]
    public void A_parenthesised_source_still_works_beside_a_parenthesised_variable()
    {
        var result = ToshParser.Parse("for (var f) in (ls | first 2) { echo $f.Name }");

        Assert.Empty(result.Diagnostics);

        var statement = Assert.IsType<ForStatementSyntax>(result.Statement);

        Assert.Equal("f", statement.VariableName);
        Assert.Equal(2, statement.Source.Stages.Count);
    }

    [Fact]
    public void A_clause_that_never_closes_says_so()
    {
        var result = ToshParser.Parse("for (var i in 1..3 { echo $i }");

        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.missing_closing_parenthesis");
    }

    [Fact]
    public async Task The_whole_clause_form_enforces_its_annotation_too()
    {
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => Engine().ExecuteToListAsync("""for (int n in ["a"]) { echo $n }"""));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.runtime.annotation_conversion_failed");
    }

    [Fact]
    public async Task An_annotated_loop_variable_converts_each_value()
    {
        // The point of the annotation: inside the body `$n` is an int, so it adds rather
        // than concatenates. Without the conversion `"1" + 1` is `"11"`.
        var results = await Engine().ExecuteToListAsync(
            """
            for (int n) in ["1", "2"] { echo ($n + 1) }
            """);

        Assert.Equal([2, 3], results.Select(value => Convert.ToInt32(value)));
    }

    [Fact]
    public async Task A_value_that_will_not_convert_is_reported()
    {
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => Engine().ExecuteToListAsync("""for (int n) in ["a"] { echo $n }"""));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.runtime.annotation_conversion_failed");
    }

    [Fact]
    public async Task An_unknown_type_is_reported_even_when_the_loop_never_runs()
    {
        // A conversion only reports an unknown type when it fails, so a loop over nothing
        // would otherwise run happily and say the annotation was fine.
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => Engine().ExecuteToListAsync("for (Nonexitent x) in [] { echo $x }"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.runtime.annotation_unknown_type");
    }

    [Fact]
    public async Task The_annotation_outlives_the_first_line_of_the_body()
    {
        // Carried into the scope rather than only applied on the way in, so an assignment
        // inside the body is checked too — otherwise the type would be a claim about where
        // the value came from rather than about the variable.
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => Engine().ExecuteToListAsync("""for (int n) in 1..2 { $n = "nope" }"""));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.runtime.annotation_conversion_failed");
    }

    [Fact]
    public async Task An_unannotated_loop_takes_whatever_it_is_given()
    {
        var results = await Engine().ExecuteToListAsync("""for x in ["a", 1] { echo $x }""");

        Assert.Equal(["a", "1"], results.Select(value => $"{value}"));
    }

    [Fact]
    public async Task The_current_item_is_left_as_it_came()
    {
        // `_` is the current item, and the current item is what it is: the reader annotated
        // a name, not the pipeline.
        var results = await Engine().ExecuteToListAsync(
            """
            for (string s) in [1] { echo ($s + "!"); echo (_ + 1) }
            """);

        Assert.Equal(["1!", "2"], results.Select(value => $"{value}"));
    }
}

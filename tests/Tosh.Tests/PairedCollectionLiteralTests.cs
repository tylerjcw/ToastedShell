using Tosh.Language;
using Tosh.Language.Parsing;

namespace Tosh.Tests;

public sealed class PairedCollectionLiteralTests
{
    [Theory]
    [InlineData("echo {||}", "record", 0)]
    [InlineData("echo {| name = toast |}", "record", 1)]
    [InlineData("echo {|name=toast|}", "record", 1)]
    [InlineData("echo {%%}", "dict", 0)]
    [InlineData("echo {% \"key\" => 7 %}", "dict", 1)]
    [InlineData("echo {%\"key\"=>7%}", "dict", 1)]
    [InlineData("echo {::}", "set", 0)]
    [InlineData("echo {: 1, 2 :}", "set", 2)]
    [InlineData("echo {:1,2:}", "set", 2)]
    public void Parser_dispatches_paired_literals_from_their_opening_token(
        string source,
        string expectedKind,
        int expectedItemCount)
    {
        var argument = ParseSingleCommandArgument(source);

        var actualItemCount = expectedKind switch
        {
            "record" => Assert.IsType<RecordLiteralArgumentSyntax>(argument).Fields.Count,
            "dict" => Assert.IsType<DictLiteralArgumentSyntax>(argument).Entries.Count,
            "set" => Assert.IsType<SetLiteralArgumentSyntax>(argument).Items.Count,
            _ => throw new InvalidOperationException($"Unknown literal kind '{expectedKind}'."),
        };

        Assert.Equal(expectedItemCount, actualItemCount);
    }

    [Fact]
    public async Task Paired_literals_evaluate_in_expression_context()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var record = {|name=toast|}
            var dict = {%"key"=>7%}
            var set = {:1,2,2:}
            echo $record.name
            echo $dict["key"]
            echo $set.Count
            """);

        Assert.Equal(new object?[] { "toast", 7, 2 }, results);
    }

    [Fact]
    public async Task Paired_literals_evaluate_as_ordinary_command_arguments()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo {| name = toast |}
            echo {% "key" => 7 %}
            echo {: 1, 2, 2 :}
            """);

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(results[0]);
        Assert.Equal("toast", record["name"]);

        var dict = Assert.IsAssignableFrom<System.Collections.IDictionary>(results[1]);
        Assert.Equal(7, dict["key"]);

        var set = Assert.IsType<HashSet<object?>>(results[2]);
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public async Task Paired_literals_evaluate_in_parenthesized_context()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo ({|name=toast|}).name
            echo ({%"key"=>7%})["key"]
            echo ({:1,2,2:}).Count
            """);

        Assert.Equal(new object?[] { "toast", 7, 2 }, results);
    }

    [Fact]
    public async Task Literal_closers_support_postfix_access_without_whitespace()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            echo {|name=toast|}.name
            echo {%"key"=>7%}["key"]
            echo {:1,2,2:}.Count
            """);

        Assert.Equal(new object?[] { "toast", 7, 2 }, results);
    }

    [Fact]
    public async Task Paired_literals_preserve_mixed_nesting_boundaries()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            var value = {|record={|n=3|},dict={%"set"=>{:1,2,2:}%}|}
            echo $value.record.n
            echo $value.dict["set"].Count
            """);

        Assert.Equal(new object?[] { 3, 2 }, results);
    }

    [Fact]
    public void Plain_braces_dispatch_as_a_block_argument()
    {
        var argument = ParseSingleCommandArgument("echo { echo toast }");

        Assert.IsType<BlockArgumentSyntax>(argument);
    }

    [Fact]
    public void Plain_braces_dispatch_as_a_block_in_expression_context()
    {
        var result = ToshParser.Parse("var value = { echo toast }");

        Assert.Empty(result.Diagnostics);
        var declaration = Assert.IsType<VariableDeclarationStatementSyntax>(result.Statement);
        Assert.NotNull(declaration.Value);
        var value = declaration.Value!;
        var stage = Assert.IsType<ExpressionPipelineStageSyntax>(Assert.Single(value.Stages));
        Assert.IsType<BlockArgumentSyntax>(stage.Expression);
    }

    [Theory]
    [InlineData("echo {| name = toast | }")]
    [InlineData("echo {% \"key\" => 7 % }")]
    [InlineData("echo {: 1, 2 : }")]
    public void Spaced_literal_closers_report_a_targeted_diagnostic_without_hanging(string source)
    {
        var result = ParseWithinBudget(source);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.parser.spaced_literal_delimiter");
    }

    [Theory]
    [InlineData("echo {| name = toast }", "tosh.parser.missing_record_closing_delimiter")]
    [InlineData("echo {% \"key\" => 7 }", "tosh.parser.missing_dict_closing_delimiter")]
    [InlineData("echo {: 1, 2 }", "tosh.parser.missing_set_closing_delimiter")]
    public void Missing_literal_closers_report_a_targeted_diagnostic_without_hanging(
        string source,
        string expectedCode)
    {
        var result = ParseWithinBudget(source);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Theory]
    [InlineData("echo {| name = toast %}", "tosh.parser.missing_record_closing_delimiter")]
    [InlineData("echo {% \"key\" => 7 :}", "tosh.parser.missing_dict_closing_delimiter")]
    [InlineData("echo {: 1, 2 |}", "tosh.parser.missing_set_closing_delimiter")]
    public void Mismatched_literal_closers_recover_without_hanging(
        string source,
        string expectedCode)
    {
        var result = ParseWithinBudget(source);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static ArgumentSyntax ParseSingleCommandArgument(string source)
    {
        var result = ToshParser.Parse(source);

        Assert.Empty(result.Diagnostics);
        var command = Assert.Single(result.Pipeline.Commands);
        return Assert.Single(command.Arguments);
    }

    private static ParseResult ParseWithinBudget(string source, int milliseconds = 2000)
    {
        var task = Task.Run(() => ToshParser.Parse(source));
        var finished = task.Wait(TimeSpan.FromMilliseconds(milliseconds));

        Assert.True(
            finished,
            $"Parser did not return within {milliseconds}ms for source: {source}");

        return task.GetAwaiter().GetResult();
    }
}

using Tosh.Language.Parsing;
using Tosh.Runtime;
using Xunit;

namespace Tosh.Tests;

public sealed class MultilineMethodChainingTests
{
    [Fact]
    public async Task Variable_method_chain_across_newlines()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            var s = "hello"
            var res = $s
                .Replace("h", "j")
                .ToUpper()
            $res
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal("JELLO", Assert.Single(results));
    }

    [Fact]
    public async Task Static_method_chain_across_newlines()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            var res = System.Text.RegularExpressions.Regex
                .Replace("foo123bar", "\d+", "-")
                .ToUpper()
            $res
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal("FOO-BAR", Assert.Single(results));
    }

    [Fact]
    public async Task Arrow_function_multiline_method_chain()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            func clean(s: string) => $s
                .Trim()
                .ToUpper()
            clean "  hello  "
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal("HELLO", Assert.Single(results));
    }

    [Fact]
    public async Task User_parse_key_style_arrow_function()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            func parse-key(k: string) => System.Globalization.CultureInfo.InvariantCulture.TextInfo
                .ToTitleCase(System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Replace($k, '([a-z])([A-Z])', '$1 $2'),
                    '[^a-zA-Z0-9]+',
                    ' '))
                .Replace(' ', '')
            parse-key "CpuInfo_model-name"
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal("CpuInfoModelName", Assert.Single(results));
    }

    [Fact]
    public async Task Parenthesized_multiline_chain()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            (
                "  hello world  "
                    .Trim()
                    .ToUpper()
            )
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal("HELLO WORLD", Assert.Single(results));
    }

    [Fact]
    public async Task Null_safe_navigation_across_newlines()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            fluid class Node(val) {
                prop Value = $val
                prop Next = null
            }
            var a = new Node("root")
            var b = $a
                ?.Next
                ?.Value
            echo ($b == null)
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal(true, Assert.Single(results));
    }

    [Fact]
    public async Task Multiline_chain_with_interleaved_comments()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            var s = "hello"
            var res = $s
                # swap h for w
                .Replace("h", "w")
                # capitalize
                .ToUpper()
            $res
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal("WELLO", Assert.Single(results));
    }

    [Fact]
    public async Task Multiline_chain_piped_to_next_stage()
    {
        var engine = ShellEngine.CreateFullShell();
        var script = """
            var key = "foo-bar-baz"
            $key
                .ToUpper()
                .Split('-')
                | join
            """;

        var results = await engine.ExecuteToListAsync(script);
        Assert.Equal("FOOBARBAZ", Assert.Single(results));
    }

    [Fact]
    public void Relative_path_on_newline_does_not_attach_as_member_access()
    {
        var script = """
            var x = "test"
            ./my_script
            """;

        var parseResult = ToshParser.Parse(script);
        Assert.Empty(parseResult.Diagnostics);
        var scriptStatement = Assert.IsType<ScriptStatementSyntax>(parseResult.Statement);
        Assert.Equal(2, scriptStatement.Statements.Count);

        var first = Assert.IsType<VariableDeclarationStatementSyntax>(scriptStatement.Statements[0]);
        Assert.Equal("x", first.Name);

        var second = Assert.IsType<PipelineStatementSyntax>(scriptStatement.Statements[1]);
        var command = Assert.IsType<CommandSyntax>(Assert.Single(second.Pipeline.Stages));
        Assert.Equal("./my_script", command.Name);
    }
}

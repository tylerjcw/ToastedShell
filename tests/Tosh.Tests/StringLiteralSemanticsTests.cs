using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class StringLiteralSemanticsTests
{
    [Theory]
    [InlineData("echo \"line\\nnext\"", "line\nnext")]
    [InlineData("echo 'line\\nnext'", "line\\nnext")]
    [InlineData("echo \"\"\"line\\nnext\"\"\"", "line\\nnext")]
    [InlineData("echo '''line\\nnext'''", "line\nnext")]
    [InlineData("echo $\"sum={1 + 1}\\n\"", "sum=2\n")]
    [InlineData("echo $\"\"\"sum={1 + 1}\\n\"\"\"", "sum=2\\n")]
    [InlineData("echo $'line\\nnext'", "line\nnext")]
    [InlineData("echo $'''sum={1 + 1}\\nend'''", "sum=2\nend")]
    public async Task Every_documented_quote_form_has_distinct_escape_and_interpolation_semantics(
        string source,
        string expected)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var result = Assert.Single(await engine.ExecuteToListAsync(source));

        Assert.Equal(expected, Assert.IsType<string>(result));
    }

    [Theory]
    [InlineData("echo \"\\d+\\q\"", "\\d+\\q")]
    [InlineData("echo $\"\\d+\\q\"", "\\d+\\q")]
    [InlineData("echo $'\\d+\\q'", "\\d+\\q")]
    [InlineData("echo '''\\d+\\q'''", "\\d+\\q")]
    [InlineData("echo $'\\x\\u'", "\\x\\u")]
    public async Task Unknown_escapes_preserve_the_backslash(
        string source,
        string expected)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var result = Assert.Single(await engine.ExecuteToListAsync(source));

        Assert.Equal(expected, Assert.IsType<string>(result));
    }

    [Fact]
    public async Task Regex_patterns_work_in_raw_and_double_quoted_strings()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            echo ("a1" =~ "\d")
            echo ("a1" =~ '\d')
            echo ("fileXcs" =~ "\.cs$")
            echo ("file.cs" =~ "\.cs$")
            """);

        Assert.Equal([true, true, false, true], results);
    }
}

using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A <c>throw</c> in expression position, in every place the specification says it
/// belongs.
/// </summary>
/// <remarks>
/// <para>
/// Found by parsing the specification's own code listings rather than reading them.
/// §Throw Expressions documents three positions side by side — a ternary arm, the
/// right of <c>??</c>, and a match arm body — and only two of them parsed. The
/// <c>??</c> case fell through to the command parser, so <c>$env.PORT ?? throw
/// "PORT is required"</c>, written verbatim in the specification, failed with
/// <c>tosh.parser.missing_pipeline_separator</c>.
/// </para>
/// <para>
/// The cause was that the throw-expression form lived inside the ternary branch
/// parser and had no other caller. It is a helper now, so a third position is a
/// call rather than a copy.
/// </para>
/// <para>
/// Short-circuiting is asserted as well as parsing, because a <c>throw</c> that is
/// merely reachable is not the same as one that is only evaluated when the left
/// operand is null — and a throw-expression that fires eagerly would pass a
/// parse-only check.
/// </para>
/// </remarks>
public sealed class ThrowExpressionPositionTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    private static string[] ParseErrors(string script) =>
        ToshParser.Parse(script, "<probe>").Diagnostics.Select(d => d.Code).ToArray();

    /// <summary>The three positions the specification documents, all parsing.</summary>
    [Theory]
    [InlineData("var x = ($valid ? $value : throw \"validation failed\")")]
    [InlineData("var conn = $connectionString ?? throw \"Connection string is required\"")]
    [InlineData("var c = ($connectionString ?? throw \"required\")")]
    [InlineData("var label = match ($input) {\n    1 => \"one\"\n    default => throw \"unexpected\"\n}")]
    public void A_throw_expression_parses_everywhere_the_specification_puts_one(string script)
    {
        Assert.Empty(ParseErrors(script));
    }

    /// <summary>
    /// The one that was broken, end to end: the left operand is null, so the throw
    /// is the value of the expression and it raises.
    /// </summary>
    [Fact]
    public async Task A_null_left_operand_raises_the_throw_on_the_right()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync("var c = (null ?? throw \"required\")\n$c"));

        Assert.Contains("required", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the half a parse check cannot see: a non-null left operand yields its own
    /// value and the throw is never evaluated.
    /// </summary>
    [Fact]
    public async Task A_non_null_left_operand_does_not_evaluate_the_throw()
    {
        var results = await RunAsync("var c = (\"present\" ?? throw \"required\")\n$c");

        Assert.Equal("present", Assert.Single(results));
    }

    /// <summary>
    /// `throw` is still a statement keyword, and the fix must not have turned every
    /// `??` right operand into one: a bareword command there is still a command.
    /// </summary>
    [Fact]
    public async Task An_ordinary_right_operand_is_unaffected()
    {
        var results = await RunAsync("var c = (null ?? \"fallback\")\n$c");

        Assert.Equal("fallback", Assert.Single(results));
    }
}

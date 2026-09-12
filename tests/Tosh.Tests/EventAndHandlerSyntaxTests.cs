using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// Two shapes the specification writes and the parser did not accept, both found by
/// parsing the specification's own code listings rather than reading them.
/// </summary>
public sealed class EventAndHandlerSyntaxTests
{
    private static string[] ParseErrors(string script) =>
        ToshParser.Parse(script, "<probe>").Diagnostics.Select(d => d.Code).ToArray();

    /// <summary>
    /// An event body puts one field per line, as every other block in the language
    /// does. It could not: the default value's pipeline ran past the end of the line
    /// and read the next field as a second stage, so
    /// <c>tosh.parser.missing_pipeline_separator</c> was reported at the second
    /// field and only semicolons worked.
    /// </summary>
    [Theory]
    [InlineData("event E { A = \"\" }")]
    [InlineData("event E { A = \"\"; B = true }")]
    [InlineData("event E {\n    A = \"\"\n    B = true\n}")]
    [InlineData("event BuildCompleted {\n    Project  = \"\"\n    Duration = (timespan 0s)\n    Success  = true\n}")]
    [InlineData("event E {\n    A: string = \"\"\n    B: bool = true\n}")]
    [InlineData("event E {\n    A: string\n    B: int\n}")]
    public void An_event_body_separates_its_fields_by_line(string script)
    {
        Assert.Empty(ParseErrors(script));
    }

    /// <summary>
    /// The half the fix must not cost: a field default may still be a real pipeline.
    /// <c>singleExpressionBody</c> would have ended the default at the <c>|</c> —
    /// right for a lambda body, wrong here — so the flag is a line test instead, and
    /// it sits after the pipe branch so a continuation line still joins.
    /// </summary>
    [Theory]
    [InlineData("event E { A = 1 | count }")]
    [InlineData("event E { A = 1\n| count }")]
    public void An_event_field_default_may_still_be_a_pipeline(string script)
    {
        Assert.Empty(ParseErrors(script));
    }

    /// <summary>
    /// A handler's three modifier clauses, in every order. They were parsed as a
    /// fixed sequence — <c>when</c>, then <c>priority</c>, then <c>once</c> — so the
    /// specification's own <c>handles CommandCompleted priority 10 when { … }</c>
    /// failed, and the error it produced named the missing block rather than the
    /// clause that was in the wrong place.
    /// </summary>
    [Theory]
    [InlineData("func h(e) handles Ev { writeline 1 }")]
    [InlineData("func h(e) handles Ev when { true } { writeline 1 }")]
    [InlineData("func h(e) handles Ev priority 10 { writeline 1 }")]
    [InlineData("func h(e) handles Ev once { writeline 1 }")]
    [InlineData("func h(e) handles Ev priority 10 when { true } { writeline 1 }")]
    [InlineData("func h(e) handles Ev when { true } priority 10 { writeline 1 }")]
    [InlineData("func h(e) handles Ev once when { true } { writeline 1 }")]
    [InlineData("func h(e) handles Ev when { true } once { writeline 1 }")]
    [InlineData("func h(e) handles Ev priority 5 once { writeline 1 }")]
    [InlineData("func h(e) handles Ev once priority 5 { writeline 1 }")]
    [InlineData("func h(e) handles Ev priority 10\n    when { $e.Duration > (timespan 1s) }\n{\n    writeline 1\n}")]
    public void A_handler_takes_its_clauses_in_any_order(string script)
    {
        Assert.Empty(ParseErrors(script));
    }

    /// <summary>
    /// The clause loop must not have swallowed the diagnostic for a bad priority:
    /// a non-integer is still named where it stands.
    /// </summary>
    [Fact]
    public void A_non_integer_priority_is_still_reported()
    {
        Assert.Contains("tosh.parser.expected_priority_value",
                        ParseErrors("func h(e) handles Ev priority \"x\" { writeline 1 }"));
    }
}

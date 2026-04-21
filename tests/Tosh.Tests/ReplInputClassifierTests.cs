using Tosh.Cli;

namespace Tosh.Tests;

public sealed class ReplInputClassifierTests
{
    [Fact]
    public void Requires_continuation_for_trailing_pipe()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(["ls |"]));
    }

    [Fact]
    public void Requires_continuation_for_unclosed_block()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(["ls | each {", "_.Name"]));
    }

    [Fact]
    public void Does_not_require_continuation_for_closed_multiline_block()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(["ls | each {", "_.Name", "}"]));
    }

    [Fact]
    public void Ignores_braces_inside_strings()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(["echo \"{\""]));
    }

    [Fact]
    public void Suggests_indentation_for_open_block()
    {
        Assert.Equal("    ", ReplInputClassifier.GetSuggestedContinuationText(["if true {"]));
    }

    [Fact]
    public void Suggests_deeper_indentation_for_indented_pipe_continuation()
    {
        Assert.Equal("        ", ReplInputClassifier.GetSuggestedContinuationText(["    ls |"]));
    }

    [Fact]
    public void Suggests_continuation_for_ternary_expression()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(["var label = $ok ?"]));
        Assert.Equal("        ", ReplInputClassifier.GetSuggestedContinuationText(["var label = $ok ?", "    yes :"]));
    }

    [Fact]
    public void String_based_continuation_helpers_match_line_based_analysis()
    {
        var source = "ls | each {\n    _.Name";

        Assert.True(ReplInputClassifier.RequiresContinuation(source));
        Assert.Equal("    ", ReplInputClassifier.GetSuggestedContinuationText(source));
    }

    [Fact]
    public void Suggests_same_indentation_inside_open_block_without_growing_each_line()
    {
        Assert.Equal("    ", ReplInputClassifier.GetSuggestedContinuationText([
            "if true {",
            "    echo one"]));

        Assert.Equal("    ", ReplInputClassifier.GetSuggestedContinuationText([
            "if true {",
            "    echo one",
            "    echo two"]));
    }

    // --- Multi-line compound statement tests (parser-based continuation) ---

    [Fact]
    public void Func_with_params_on_separate_lines_requires_continuation()
    {
        // func projectGuard(
        //     var1: int,
        //     var2: string,
        //     var3: bool
        // )
        // Parens closed, but no body yet → continue
        Assert.True(ReplInputClassifier.RequiresContinuation([
            "func projectGuard(",
            "    var1: int,",
            "    var2: string,",
            "    var3: bool",
            ")"]));
    }

    [Fact]
    public void Func_with_multiline_params_and_body_on_next_line_requires_continuation()
    {
        // After ")" we still need "{ ... }"
        Assert.True(ReplInputClassifier.RequiresContinuation([
            "func projectGuard(",
            "    var1: int,",
            "    var2: string, var3: bool)"]));
    }

    [Fact]
    public void Func_with_multiline_params_and_complete_body_does_not_require_continuation()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation([
            "func projectGuard(",
            "    var1: int,",
            "    var2: string,",
            "    var3: bool",
            ")",
            "{",
            "    writeline \"entering project dir\"",
            "}"]));
    }

    [Fact]
    public void Func_with_body_on_same_line_as_close_paren_is_complete()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation([
            "func projectGuard(",
            "    var1: int,",
            "    var2: string,",
            "    var3: bool",
            ") { writeline \"entering project dir\" }"]));
    }

    [Fact]
    public void Func_with_inline_body_is_complete()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(
            ["func projectGuard(var1: int, var2: string, var3: bool) { writeline \"entering project dir\" }"]));
    }

    [Fact]
    public void Func_arrow_wrapper_is_complete()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(
            ["func someAlias(arg1, arg2) => ls -la $arg1 | where Type == $arg2"]));
    }

    [Fact]
    public void Func_with_multiline_body_is_complete()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation([
            "func projectGuard(var1: int, var2: string, var3: bool) {",
            "    writeline \"entering project dir\"",
            "}"]));
    }

    [Fact]
    public void Func_with_handles_requires_continuation()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(
            ["func onError(evt) handles StatusUpdate"]));
    }

    [Fact]
    public void Func_with_handles_and_when_guard_requires_continuation()
    {
        // when guard closes its braces, but body is still missing
        Assert.True(ReplInputClassifier.RequiresContinuation([
            "func onError(evt)",
            "    handles StatusUpdate",
            "    when { $evt.Level == \"error\" }"]));
    }

    [Fact]
    public void Func_with_handles_when_and_body_is_complete()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation([
            "func onError(evt)",
            "    handles StatusUpdate",
            "    when { $evt.Level == \"error\" }",
            "{",
            "    writeline \"error!\"",
            "}"]));
    }

    [Fact]
    public void If_without_body_requires_continuation()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(["if ($x > 5)"]));
    }

    [Fact]
    public void For_without_body_requires_continuation()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(["for item in (ls)"]));
    }

    [Fact]
    public void Class_with_open_brace_requires_continuation()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(["class MyClass {"]));
    }

    [Fact]
    public void Simple_pipeline_does_not_trigger_parser_continuation()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(["echo hello | type-of"]));
    }

    [Fact]
    public void Variable_assignment_does_not_trigger_parser_continuation()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(["var x = 42"]));
    }

    [Fact]
    public void Event_definition_with_open_brace_requires_continuation()
    {
        Assert.True(ReplInputClassifier.RequiresContinuation(["event StatusUpdate {"]));
    }

    [Fact]
    public void Event_definition_with_body_is_complete()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation([
            "event StatusUpdate {",
            "    Level = \"info\"",
            "}"]));
    }
}

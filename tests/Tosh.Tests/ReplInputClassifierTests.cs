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
        Assert.True(ReplInputClassifier.RequiresContinuation(["ls | each {", "$it.Name"]));
    }

    [Fact]
    public void Does_not_require_continuation_for_closed_multiline_block()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(["ls | each {", "$it.Name", "}"]));
    }

    [Fact]
    public void Ignores_braces_inside_strings()
    {
        Assert.False(ReplInputClassifier.RequiresContinuation(["echo \"{\""]));
    }
}

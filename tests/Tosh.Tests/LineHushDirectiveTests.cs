using System.IO;
using System.Threading.Tasks;
using Tosh.Runtime;
using Tosh.Language;
using Xunit;

namespace Tosh.Tests;

public sealed class LineHushDirectiveTests
{
    [Fact]
    public async Task Inline_hush_comment_suppresses_warning_on_same_line()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errorWriter = new StringWriter();
        runtime.Error = errorWriter;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            "var _ = 1\n" +
            "var _ = 2  # hush tosh.naming.shadowed_underscore\n");

        Assert.DoesNotContain("shadowed_underscore", errorWriter.ToString());
    }

    [Fact]
    public async Task Hush_comment_on_previous_line_also_suppresses_warning()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errorWriter = new StringWriter();
        runtime.Error = errorWriter;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            "var _ = 1\n" +
            "# hush tosh.naming.shadowed_underscore\n" +
            "var _ = 2\n");

        Assert.DoesNotContain("shadowed_underscore", errorWriter.ToString());
    }

    [Fact]
    public async Task Warning_still_emits_when_hush_targets_a_different_code()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errorWriter = new StringWriter();
        runtime.Error = errorWriter;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync(
            "var _ = 1\n" +
            "var _ = 2  # hush tosh.naming.shadowed_builtin\n");

        Assert.Contains("shadowed_underscore", errorWriter.ToString());
    }
}

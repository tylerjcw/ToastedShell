using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class RuntimeNamespaceDisplayTests
{
    [Fact]
    public async Task Root_tosh_renders_as_runtime_summary()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        var values = await engine.ExecuteToListAsync("$tosh");
        var rendered = StyledText.StripAnsi(runtime.Display.RenderMany(values));

        Assert.Contains("$tosh", rendered, StringComparison.Ordinal);
        Assert.Contains("Live Runtime Namespace", rendered, StringComparison.Ordinal);
        Assert.Contains("Host Namespace", rendered, StringComparison.Ordinal);
        Assert.Contains("$tosh.<Member>", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Piped_tosh_to_json_still_returns_full_snapshot()
    {
        var engine = ShellEngine.CreateFullShell();

        var values = await engine.ExecuteToListAsync("$tosh | to json");
        var json = string.Join("\n", values.Select(value => value?.ToString() ?? string.Empty));

        Assert.Contains("\"Config\":", json, StringComparison.Ordinal);
        Assert.Contains("\"Host\":", json, StringComparison.Ordinal);
        Assert.Contains("\"Session\":", json, StringComparison.Ordinal);
    }
}
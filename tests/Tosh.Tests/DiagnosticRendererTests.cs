using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class DiagnosticRendererTests
{
    [Fact]
    public async Task Single_equals_in_where_renders_a_pointed_diagnostic()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "alpha.txt"), "alpha");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);
        var renderer = new DiagnosticRenderer();

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync("ls | where _.Type = file", "repl_entry #1"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("repl_entry #1", diagnostic.SourceName);

        var text = renderer.Render(exception);

        Assert.Contains("ls | where _.Type = file", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_reports_unterminated_strings_as_structured_diagnostics()
    {
        var engine = new ToshEngine();

        var exception = Assert.Throws<ToshDiagnosticException>(() =>
            engine.EvaluateAsync("echo \"unterminated", "repl_entry #2"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh::parser::unterminated_string", diagnostic.Code);
        Assert.Equal("repl_entry #2", diagnostic.SourceName);
    }

    [Fact]
    public void Diagnostic_renderer_can_apply_runtime_theme_colors()
    {
        var theme = new ToshDiagnosticThemeConfig();
        theme.Heading.Foreground = "bright-yellow";
        theme.Title.Foreground = "bright-magenta";
        var renderer = new DiagnosticRenderer(theme);

        var text = renderer.Render(ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh::test",
            Title: "Boom")));

        Assert.Contains("\x1b[1;93mError: tosh::test\x1b[0m", text);
        Assert.Contains("\x1b[95m  × Boom\x1b[0m", text);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-diagnostic-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

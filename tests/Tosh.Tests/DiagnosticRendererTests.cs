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
            await engine.ExecuteToListAsync("ls | where Type = file", "repl_entry #1"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh::parser::assignment_requires_variable", diagnostic.Code);
        Assert.Equal("repl_entry #1", diagnostic.SourceName);

        var text = renderer.Render(exception);

        Assert.Contains("Error: tosh::parser::assignment_requires_variable", text, StringComparison.Ordinal);
        Assert.Contains("Assignment operations require a variable.", text, StringComparison.Ordinal);
        Assert.Contains("ls | where Type = file", text, StringComparison.Ordinal);
        Assert.Contains("use '==' for equality comparisons in 'where'", text, StringComparison.Ordinal);
        Assert.Contains("help: try `where Type == file`", text, StringComparison.Ordinal);
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

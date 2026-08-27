using Tosh.Runtime;
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
        var engine = new ToshEngine(runtime.Language);
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
        var engine = ShellEngine.CreateFullShell();

        var exception = Assert.Throws<ToshDiagnosticException>(() =>
            engine.EvaluateAsync("echo \"unterminated", "repl_entry #2"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.parser.unterminated_string", diagnostic.Code);
        Assert.Equal("repl_entry #2", diagnostic.SourceName);
    }

    [Fact]
    public void Diagnostic_renderer_can_apply_runtime_theme_colors()
    {
        var theme = new ToshDiagnosticThemeConfig();
        theme.ErrorGlyph.Foreground = "bright-yellow";
        theme.Title.Foreground = "bright-magenta";
        var renderer = new DiagnosticRenderer(theme);

        var text = renderer.Render(ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.test",
            Title: "Boom")));

        // Header carries the severity word and the diagnostic code.
        Assert.Contains("error", text, StringComparison.Ordinal);
        Assert.Contains("tosh.test", text, StringComparison.Ordinal);

        if (TerminalEnvironmentTestSupport.DiagnosticsPlainModeIsActive)
        {
            Assert.Contains("error[tosh.test]: Boom", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1b[", text, StringComparison.Ordinal);
            return;
        }

        // The error glyph is styled bright-yellow + bold (default ErrorGlyph is bold).
        Assert.Contains("\x1b[1;93m", text);

        // The title is rendered with the theme's Title color.
        Assert.Contains("\x1b[95mBoom\x1b[0m", text);
    }

    [Fact]
    public void Diagnostic_renderer_emits_hush_hint_for_warnings_with_codes()
    {
        var renderer = new DiagnosticRenderer();
        var text = renderer.Render(new ToshDiagnostic(
            Code: "tosh.naming.shadowed_underscore",
            Title: "shadows builtin",
            Severity: ToshDiagnosticSeverity.Warning));

        Assert.Contains("hush:", text, StringComparison.Ordinal);
        Assert.Contains("tosh.naming.shadowed_underscore", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_renderer_does_not_emit_hush_hint_for_errors()
    {
        var renderer = new DiagnosticRenderer();
        var text = renderer.Render(new ToshDiagnostic(
            Code: "tosh.runtime.error",
            Title: "Boom",
            Severity: ToshDiagnosticSeverity.Error));

        Assert.DoesNotContain("hush:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_renderer_plain_mode_uses_gcc_shape()
    {
        var renderer = new DiagnosticRenderer(theme: null, config: null, forcePlain: true);

        var text = renderer.Render(new ToshDiagnostic(
            Code: "tosh.test.boom",
            Title: "kaboom",
            Help: "try again"));

        // GCC-shaped: severity[code]: title
        Assert.Contains("error[tosh.test.boom]: kaboom", text, StringComparison.Ordinal);
        Assert.Contains("help: try again", text, StringComparison.Ordinal);

        // No ANSI escapes.
        Assert.DoesNotContain("\x1b[", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_renderer_json_mode_emits_ndjson()
    {
        var renderer = new DiagnosticRenderer(theme: null, config: null, forceJson: true);

        var text = renderer.Render(new ToshDiagnostic(
            Code: "tosh.test.boom",
            Title: "kaboom",
            Severity: ToshDiagnosticSeverity.Warning));

        Assert.StartsWith("{", text, StringComparison.Ordinal);
        Assert.EndsWith("}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"warn\"", text, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"tosh.test.boom\"", text, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"kaboom\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_renderer_emits_osc8_hyperlink_when_help_uri_base_set()
    {
        var diagnosticsConfig = new ToshDiagnosticsConfig(new ToastOptions())
        {
            HelpUriBase = "https://tosh.dev/d",
        };
        var renderer = new DiagnosticRenderer(theme: new ToshDiagnosticThemeConfig(), config: diagnosticsConfig);

        var text = renderer.Render(new ToshDiagnostic(
            Code: "tosh.test.boom",
            Title: "kaboom"));

        if (TerminalEnvironmentTestSupport.DiagnosticsPlainModeIsActive)
        {
            Assert.Contains("error[tosh.test.boom]: kaboom", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\x1b]8;;", text, StringComparison.Ordinal);
            return;
        }

        Assert.Contains("\x1b]8;;https://tosh.dev/d/tosh.test.boom\x1b\\", text, StringComparison.Ordinal);
        Assert.Contains("\x1b]8;;\x1b\\", text, StringComparison.Ordinal);
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

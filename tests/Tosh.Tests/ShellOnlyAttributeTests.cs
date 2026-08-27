using System.IO;
using System.Threading.Tasks;
using Tosh.Runtime;
using Tosh.Language;
using Xunit;

namespace Tosh.Tests;

public sealed class ShellOnlyAttributeTests
{
    [Fact]
    public async Task Shell_only_command_throws_when_engine_is_not_interactive()
    {
        var engine = ShellEngine.CreateFullShell(); // IsInteractiveSession defaults to false

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("prompt-time", "<test>"));

        Assert.Contains(ex.Diagnostics, d => d.Code == "tosh.shell_only");
        Assert.Contains(ex.Diagnostics, d => d.Title.Contains("prompt-time"));
    }

    [Fact]
    public async Task Shell_only_command_runs_silently_in_interactive_sessions()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errorWriter = new StringWriter();
        runtime.Error = errorWriter;
        var engine = new ToshEngine(runtime.Language) { IsInteractiveSession = true };

        await engine.ExecuteToListAsync("prompt-time", "<test>");

        Assert.DoesNotContain("shell-only", errorWriter.ToString());
        Assert.DoesNotContain("shell_only", errorWriter.ToString());
    }

    [Fact]
    public async Task Shell_only_diagnostic_carries_attribute_help_text()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("history", "<test>"));

        var diagnostic = Assert.Single(ex.Diagnostics);
        Assert.Equal("tosh.shell_only", diagnostic.Code);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Help));
    }

    [Fact]
    public async Task Shell_only_error_is_not_hushable()
    {
        // Errors are never suppressible by design (see ToshEngine.IsCodeHushed).
        // An inline `# hush tosh.shell_only` directive must still abort.
        var engine = ShellEngine.CreateFullShell();

        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                "prompt-time  # hush tosh.shell_only\n",
                "<test>"));
    }
}

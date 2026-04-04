using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class PipelineHardeningTests
{
    [Fact]
    public async Task Shell_commands_record_exit_code_zero_in_pipeline_tracker()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("echo hello | type-of");

        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task Mixed_pipeline_uses_last_shell_command_exit_code()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        // First set a non-zero exit code
        await engine.ExecuteToListAsync("/bin/sh -c \"exit 7\"");
        Assert.Equal(7, runtime.LastExitCode);

        // Then run a pure shell pipeline — should reset to 0
        await engine.ExecuteToListAsync("echo hello | type-of");
        Assert.Equal(0, runtime.LastExitCode);
    }

    [Fact]
    public async Task Mixed_pipeline_with_pipefail_detects_external_failure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        runtime.Config.Shell.Pipefail = true;

        // external (exit 7) | shell (succeeds) → with pipefail, rightmost non-zero is 7
        await engine.ExecuteToListAsync("/bin/sh -c \"echo ok; exit 7\" | type-of");
        Assert.Equal(7, runtime.LastExitCode);
    }

    [Fact]
    public async Task Redirection_to_unwritable_path_produces_diagnostic()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("echo hello out> /proc/nonexistent/impossible/file.txt"));
        Assert.Contains("redirection", exception.Diagnostics[0].Title, StringComparison.OrdinalIgnoreCase);
    }
}

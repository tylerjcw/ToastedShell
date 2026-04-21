using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class SpawnCommandTests
{
    [Fact]
    public async Task Spawn_starts_external_job_and_wait_for_completes_it()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var job = spawn dotnet --version
            $job | type-of | get Name
            wait-for $job | first | get Status
            """);

        Assert.Equal("ShellJobInfo", results[0]);
        Assert.Equal(ShellJobStatus.Completed, Assert.IsType<ShellJobStatus>(results[1]));
    }

    [Fact]
    public async Task Spawn_throws_for_unknown_external_command()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync("spawn this-command-does-not-exist-anywhere-123456789");
        });

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

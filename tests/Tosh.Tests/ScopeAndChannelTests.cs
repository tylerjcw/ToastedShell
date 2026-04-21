using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ScopeAndChannelTests
{
    // ── scope ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scope_awaits_spawned_jobs_and_returns_completions()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            scope {
                var j1 = spawn dotnet --version
                var j2 = spawn dotnet --list-runtimes
            }
            """);

        // Both completions come back, ordered by job ID.
        Assert.Equal(2, results.Count);
        foreach (var item in results)
        {
            var completion = Assert.IsType<ShellJobCompletion>(item);
            Assert.Equal(ShellJobStatus.Completed, completion.Status);
        }
    }

    [Fact]
    public async Task Scope_kills_jobs_and_rethrows_when_block_throws()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                scope {
                    var j1 = spawn dotnet --version
                    throw "scope block failed"
                }
                """);
        });

        // After scope throws, the runtime should have no running jobs.
        // (The job may have completed or been killed; either way it's gone from GetJobs())
        var remaining = engine.Runtime.GetJobs()
            .Where(j => j.Status == ShellJobStatus.Running)
            .ToList();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Scope_with_no_spawned_jobs_returns_empty()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("scope { echo hello }");

        Assert.Empty(results);
    }

    // ── channel ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Channel_creates_unbounded_channel()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var ch = channel
            $ch | type-of | get Name
            """);

        Assert.Equal("ShellChannel", results[0]);
    }

    [Fact]
    public async Task Channel_creates_bounded_channel()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var ch = channel 5
            $ch | type-of | get Name
            """);

        Assert.Equal("ShellChannel", results[0]);
    }

    [Fact]
    public async Task Channel_send_recv_roundtrip()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var ch = channel
            channel-send $ch hello
            channel-send $ch world
            channel-close $ch
            channel-recv $ch
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal("hello", results[0]);
        Assert.Equal("world", results[1]);
    }

    [Fact]
    public async Task Channel_send_pipeline_input()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var ch = channel
            echo alpha beta gamma | channel-send $ch
            channel-close $ch
            channel-recv $ch
            """);

        Assert.Equal(3, results.Count);
        Assert.Equal("alpha", results[0]);
        Assert.Equal("beta", results[1]);
        Assert.Equal("gamma", results[2]);
    }

    [Fact]
    public async Task Channel_recv_completes_when_closed_before_send()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Close a fresh channel with no items → recv should return nothing.
        var results = await engine.ExecuteToListAsync(
            """
            var ch = channel
            channel-close $ch
            channel-recv $ch
            """);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Channel_close_is_idempotent()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Closing twice must not throw.
        await engine.ExecuteToListAsync(
            """
            var ch = channel
            channel-close $ch
            channel-close $ch
            """);
    }
}

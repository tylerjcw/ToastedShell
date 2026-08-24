using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ScopeAndChannelTests
{
    /// <summary>
    /// Renders everything a <see cref="ShellJobCompletion"/> knows, for failure messages.
    /// </summary>
    /// <remarks>
    /// <c>TS-P2-39</c>: this test failed three times under parallel load and never in
    /// isolation, and each sighting reported only "Expected Completed, Actual Failed" —
    /// which is not enough to tell a child that could not start from one that ran and exited
    /// non-zero. The completion already carries the exit code, stderr, and duration; the
    /// assertions simply discarded them. Six full-suite runs since, three of them at 32
    /// parallel threads, have not reproduced it, so the next sighting may be the only
    /// evidence available for a while — it should arrive complete.
    /// </remarks>
    private static string Describe(ShellJobCompletion completion) =>
        $"job {completion.Id} '{completion.Command}': status={completion.Status}, "
        + $"exit={completion.ExitCode?.ToString() ?? "none"}, pid={completion.ProcessId?.ToString() ?? "none"}, "
        + $"duration={completion.Duration.TotalMilliseconds:F0}ms, "
        + $"stderr=[{string.Join(" | ", completion.ErrorLines)}]";

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

        var completions = results.Select(Assert.IsType<ShellJobCompletion>).ToList();

        // Both completions come back, ordered by job ID.
        Assert.True(
            completions.Count == 2,
            $"expected 2 completions, got {completions.Count}:\n  "
            + string.Join("\n  ", completions.Select(Describe)));

        foreach (var completion in completions)
        {
            Assert.True(
                completion.Status == ShellJobStatus.Completed,
                $"a scope-owned job did not complete — {Describe(completion)}");
        }
    }

    [Fact]
    public async Task Scope_kills_jobs_and_rethrows_when_block_throws()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var spawnCommand = OperatingSystem.IsWindows()
            ? "spawn ping -n 31 127.0.0.1"
            : "spawn sleep 30";

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(
                $$"""
                scope {
                    var j1 = {{spawnCommand}}
                    var j2 = {{spawnCommand}}
                    throw "scope block failed"
                }
                """);
        });
        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.throw", diagnostic.Code);
        Assert.Equal("scope block failed", diagnostic.Title);

        // Both commands deliberately outlive the block, so scope must signal them all
        // before awaiting their monitors; sequential kill-and-wait would leave the
        // second process running while the first one shuts down.
        var jobs = engine.Runtime.GetJobsSnapshot();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(ShellJobStatus.Cancelled, job.Status);
            Assert.NotNull(job.EndedAt);
        });

        // The normal listing path can now reap the terminal job immediately.
        Assert.Empty(engine.Runtime.GetJobs());
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

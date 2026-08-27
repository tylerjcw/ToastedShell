using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>await</c> over CLR awaitables — <c>TS-P1-27</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reported from real code: <c>async { $p.SendPingAsync("8.8.8.8", 1000) }</c> then
/// <c>await $reply</c> produced a value that displayed as
/// <c>AsyncStateMachineBox`1</c>. ToastScript's concurrency system and the CLR's did
/// not meet — <c>async</c>/<c>await</c> are builtin commands over <c>ShellFuture</c>,
/// and a CLR method returning a task was never awaited by anything. The only route
/// out was <c>.Result</c>, which blocks.
/// </para>
/// <para>
/// The directive was that ToastScript's async/await be the same as, or compatible
/// with, the CLR's, and the decision was C#-identical: a task-returning call yields a
/// task, and you await it. Auto-awaiting at the call site was rejected — it would
/// remove the ability to hold a task and start work concurrently, and member
/// invocation exists on both surfaces of the dual-surface interfaces, so it would have
/// to land twice.
/// </para>
/// <para>
/// <c>ConcurrencyCommandTests</c> covers the commands themselves; this file is about
/// interop, which is why it is separate.
/// </para>
/// </remarks>
public sealed class ClrAwaitTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        return await engine.ExecuteToListAsync(source);
    }

    [Fact]
    public async Task Await_yields_the_result_of_a_generic_task()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tosh-await-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "payload");

        try
        {
            var results = await RunAsync(
                $"await (System.IO.File.ReadAllTextAsync(\"{path}\"))");

            Assert.Equal("payload", Assert.Single(results));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Await_on_a_void_task_emits_nothing()
    {
        // A method declared to return plain `Task` is compiled to
        // AsyncStateMachineBox<VoidTaskResult>, so reading a `Result` property off the
        // runtime type finds one holding an internal "no value" struct. This asserts
        // the awaited type is taken from the declared generic argument instead —
        // without that, every void async method would emit garbage.
        var path = Path.Combine(Path.GetTempPath(), $"tosh-await-{Guid.NewGuid():N}.txt");

        try
        {
            var results = await RunAsync(
                $"await (System.IO.File.WriteAllTextAsync(\"{path}\", \"written\"))");

            Assert.Empty(results);
            Assert.Equal("written", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Await_flattens_a_future_whose_output_is_a_task()
    {
        // The reported shape. One `await` unwraps the future *and* the task inside it.
        // A deliberate departure from C#, where this needs Task.Unwrap: a
        // future-of-task has no use, and leaving it unflattened is what produced the
        // original report.
        var path = Path.Combine(Path.GetTempPath(), $"tosh-await-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "flattened");

        try
        {
            var results = await RunAsync(
                "var f = async { System.IO.File.ReadAllTextAsync(\"" + path + "\") }\n"
                + "await $f");

            Assert.Equal("flattened", Assert.Single(results));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_faulted_task_reports_the_inner_message()
    {
        // AggregateException's own message is "One or more errors occurred", which is
        // useless in the `catch (e)` block this lands in. One layer is unwrapped so
        // $e.Message is what the operation actually failed with.
        var results = await RunAsync(
            """
            try {
                await (System.IO.File.ReadAllTextAsync("/nonexistent/tosh/nope.txt"))
            } catch (e) {
                $e.Message
            }
            """);

        var message = Assert.Single(results)?.ToString();

        Assert.DoesNotContain("One or more errors occurred", message);
        Assert.Contains("nope.txt", message);
    }

    [Fact]
    public async Task A_future_from_a_pure_tosh_block_still_works()
    {
        // The path that worked before. Flattening must not have disturbed it.
        var results = await RunAsync(
            """
            var f = async { 40 + 2 }
            await $f
            """);

        Assert.Equal(42, Assert.Single(results));
    }

    [Fact]
    public async Task Result_still_works_so_existing_scripts_keep_running()
    {
        // `.Result` was the only route before this change, so anyone who found it is
        // relying on it. It blocks, and the documentation now says so, but it must not
        // have stopped working.
        var path = Path.Combine(Path.GetTempPath(), $"tosh-await-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "blocking");

        try
        {
            var results = await RunAsync(
                $"""
                var t = System.IO.File.ReadAllTextAsync("{path}")
                $t.Result
                """);

            Assert.Equal("blocking", Assert.Single(results));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_task_can_be_awaited_from_the_pipeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tosh-await-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "piped");

        try
        {
            var results = await RunAsync(
                $"System.IO.File.ReadAllTextAsync(\"{path}\") | await");

            Assert.Equal("piped", Assert.Single(results));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Tasks_stay_first_class_so_work_can_overlap()
    {
        // The reason explicit await was chosen over auto-awaiting at the call site: a
        // task has to remain a *value*, or two operations cannot be in flight
        // together.
        //
        // Asserted as that property rather than by wall clock. The first version of
        // this test timed two 200ms delays against a 380ms budget and failed inside
        // the full parallel suite while passing alone — a timing assertion under
        // parallel test load measures the machine, not the code. Holding two
        // un-awaited tasks simultaneously is the enabling property; overlap follows
        // from it deterministically.
        var results = await RunAsync(
            """
            var a = System.Threading.Tasks.Task.Delay(20)
            var b = System.Threading.Tasks.Task.Delay(20)
            $a
            $b
            """);

        Assert.Equal(2, results.Count);
        Assert.All(results, value => Assert.True(
            ClrAwaitable.IsAwaitable(value),
            $"expected an un-awaited task, got {value?.GetType().Name ?? "null"}"));
    }

    [Fact]
    public async Task Awaiting_a_non_awaitable_is_refused_with_guidance()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await engine.ExecuteToListAsync("await 42"));

        Assert.Contains(
            "tosh.runtime.await_requires_future",
            error.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public async Task An_unawaited_task_describes_itself()
    {
        // The visible half of the defect. Explicit await means a forgotten await has to
        // be legible, and `AsyncStateMachineBox`1` was not.
        var path = Path.Combine(Path.GetTempPath(), $"tosh-await-{Guid.NewGuid():N}.txt");

        try
        {
            var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
            var results = await engine.ExecuteToListAsync(
                $"System.IO.File.ReadAllTextAsync(\"{path}\")");

            var rendered = engine.Shell().Formatter.Format(Assert.Single(results));

            Assert.StartsWith("Task<String>", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("AsyncStateMachineBox", rendered, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

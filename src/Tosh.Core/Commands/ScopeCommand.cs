namespace Tosh.Core.Commands;

[CommandCategory("Concurrency")]
[CommandArgument("block", "A block that may start background jobs via spawn.")]
[CommandExample(
    "scope { var j1 = spawn dotnet --version; var j2 = spawn dotnet --list-runtimes }",
    Title = "Start two background jobs and await both automatically")]
[CommandOutput("Streams ShellJobCompletion records for every job started inside the scope.")]
[CommandNote("All jobs registered during block execution are awaited on scope exit. If the block throws, every scope-owned job is killed before the exception propagates.")]
public sealed class ScopeCommand : ShellCommand
{
    public ScopeCommand()
        : base("scope", "Executes a block and automatically awaits all background jobs started inside it.", "scope <block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::scope_requires_block",
                title: "'scope' requires a block argument.",
                label: "pass a block like '{ spawn dotnet --version }'");
        }

        var block = context.Arguments[0];
        if (block is not ShellBlock)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::scope_requires_block",
                title: "'scope' requires a block, not a callable.",
                argumentIndex: 0,
                label: "pass a block like '{ spawn dotnet --version }'");
        }

        // Snapshot job IDs that exist BEFORE the block runs.
        var preExistingIds = context.Runtime.GetJobs()
            .Select(j => j.Id)
            .ToHashSet();

        // Execute the block.
        List<ShellJob> scopedJobs;
        try
        {
            await FunctionalCommandUtilities.ExecuteAsync(
                context,
                block,
                Array.Empty<object?>(),
                new Dictionary<string, object?>(StringComparer.Ordinal));
        }
        catch
        {
            // Block threw — collect scope-owned jobs and kill them before rethrowing.
            scopedJobs = CollectScopedJobs(context.Runtime, preExistingIds);
            foreach (var job in scopedJobs)
            {
                job.Kill();
            }
            throw;
        }

        // Block completed — gather any jobs registered during the block.
        scopedJobs = CollectScopedJobs(context.Runtime, preExistingIds);

        // Await all scope-owned jobs concurrently and stream completions.
        if (scopedJobs.Count == 0)
        {
            yield break;
        }

        var completionTasks = scopedJobs
            .Select(j => j.WaitAsync(context.CancellationToken))
            .ToArray();

        var completions = await Task.WhenAll(completionTasks);
        foreach (var completion in completions.OrderBy(c => c.Id))
        {
            yield return completion;
        }
    }

    private static List<ShellJob> CollectScopedJobs(ToshRuntime runtime, HashSet<int> preExistingIds)
    {
        return runtime.GetJobs()
            .Where(j => !preExistingIds.Contains(j.Id))
            .ToList();
    }
}

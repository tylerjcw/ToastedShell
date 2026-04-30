using System.Diagnostics;
using System.Runtime.CompilerServices;

using Tosh.Runtime;

namespace Tosh.Stdlib.Time;

/// <summary>
/// Measures wall-clock time, CPU time, memory, and page faults for a command or block,
/// matching the metrics reported by /usr/bin/time.
/// </summary>
[CommandCategory("Shell")]
[CommandArgument("command|block [args...]", "A block like '{ expr }' or a command followed by its arguments.")]
[CommandExample("time { seq 1000000 | reduce 0 { $acc + _ } }")]
[CommandExample("time ls /usr/lib")]
[CommandNote("Metrics include wall time, user/system CPU, memory, and page faults.")]
[CommandOutput("Returns a CommandTimingInfo record with timing and resource metrics.")]
public sealed class TimeCommand : ShellCommand
{
    public TimeCommand()
        : base("time", "Measures the execution time and resource usage of a command or block.", "time <command|block> [args...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.time_requires_argument",
                title: "'time' requires a command or block to measure.",
                label: "pass a block like '{ ... }' or a command name");
        }

        var target = context.Arguments[0]
            ?? throw context.CreateDiagnostic(
                code: "tosh.runtime.time_requires_argument",
                title: "'time' requires a non-null command or block to measure.",
                argumentIndex: 0,
                label: "this value is null");

        var snapshot = ProcessSnapshot.Capture();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var value in ExecuteTargetAsync(context, target).WithCancellation(context.CancellationToken))
            {
                yield return value;
            }
        }
        finally
        {
            stopwatch.Stop();
        }

        var after = ProcessSnapshot.Capture();

        yield return BuildTimingInfo(stopwatch.Elapsed, snapshot, after);
    }

    private static async IAsyncEnumerable<object?> ExecuteTargetAsync(
        CommandContext context,
        object target,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (target is ShellBlock block)
        {
            var executor = context.Runtime.BlockExecutor
                ?? throw new InvalidOperationException("Block execution is not available in this runtime.");

            await foreach (var value in executor.ExecuteAsync(
                               block,
                               new Dictionary<string, object?>(StringComparer.Ordinal),
                               context.CancellationToken)
                               .WithCancellation(context.CancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        if (target is IShellCallable callable)
        {
            var forwardArgs = context.Arguments.Skip(1).ToList();
            var innerContext = new CommandContext(
                context.Runtime,
                context.Input,
                forwardArgs,
                context.CancellationToken,
                context.Invocation,
                context.IsPipelined,
                context.ScopedTypeResolver,
                context.PipelineExitStatusTracker);

            await foreach (var value in callable.InvokeAsync(innerContext)
                               .WithCancellation(context.CancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        if (target is IShellCommand command)
        {
            var forwardArgs = context.Arguments.Skip(1).ToList();
            var innerContext = new CommandContext(
                context.Runtime,
                context.Input,
                forwardArgs,
                context.CancellationToken,
                context.Invocation,
                context.IsPipelined,
                context.ScopedTypeResolver,
                context.PipelineExitStatusTracker);

            await foreach (var value in command.ExecuteAsync(innerContext)
                               .WithCancellation(context.CancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        if (target is string commandName && context.Runtime.Commands.TryGet(commandName, out var resolved))
        {
            var forwardArgs = context.Arguments.Skip(1).ToList();
            var innerContext = new CommandContext(
                context.Runtime,
                context.Input,
                forwardArgs,
                context.CancellationToken,
                context.Invocation,
                context.IsPipelined,
                context.ScopedTypeResolver,
                context.PipelineExitStatusTracker);

            await foreach (var value in resolved.ExecuteAsync(innerContext)
                               .WithCancellation(context.CancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.time_invalid_target",
            title: "'time' requires a block, callable, or command as its first argument.",
            argumentIndex: 0,
            label: "this value is not executable",
            help: "pass a block like '{ ... }' or a command name.");
    }

    private static CommandTimingInfo BuildTimingInfo(TimeSpan elapsed, ProcessSnapshot before, ProcessSnapshot after)
    {
        var userCpu = after.UserCpuTime - before.UserCpuTime;
        var sysCpu = after.SystemCpuTime - before.SystemCpuTime;
        var totalCpu = userCpu + sysCpu;
        var cpuPercent = elapsed.TotalMilliseconds > 0
            ? totalCpu.TotalMilliseconds / elapsed.TotalMilliseconds * 100.0
            : 0.0;

        return new CommandTimingInfo
        {
            Elapsed = elapsed,
            UserCpuTime = userCpu,
            SystemCpuTime = sysCpu,
            CpuPercent = Math.Round(cpuPercent, 1),
            PeakWorkingSet = StorageSize.FromBytes(after.PeakWorkingSet),
            WorkingSetDelta = StorageSize.FromBytes(after.WorkingSet - before.WorkingSet),
            ThreadAllocations = StorageSize.FromBytes(after.ThreadAllocatedBytes - before.ThreadAllocatedBytes),
            MinorPageFaults = after.MinorPageFaults - before.MinorPageFaults,
            MajorPageFaults = after.MajorPageFaults - before.MajorPageFaults,
        };
    }

    private readonly struct ProcessSnapshot
    {
        public TimeSpan UserCpuTime { get; private init; }

        public TimeSpan SystemCpuTime { get; private init; }

        public long WorkingSet { get; private init; }

        public long PeakWorkingSet { get; private init; }

        public long ThreadAllocatedBytes { get; private init; }

        public long MinorPageFaults { get; private init; }

        public long MajorPageFaults { get; private init; }

        public static ProcessSnapshot Capture()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            ReadPageFaults(out var minorFaults, out var majorFaults);

            return new ProcessSnapshot
            {
                UserCpuTime = process.UserProcessorTime,
                SystemCpuTime = process.PrivilegedProcessorTime,
                WorkingSet = process.WorkingSet64,
                PeakWorkingSet = process.PeakWorkingSet64,
                ThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread(),
                MinorPageFaults = minorFaults,
                MajorPageFaults = majorFaults,
            };
        }

        private static void ReadPageFaults(out long minorFaults, out long majorFaults)
        {
            minorFaults = 0;
            majorFaults = 0;

            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            try
            {
                // /proc/self/stat fields (1-indexed after splitting on space past the comm field):
                //   Field 10: minflt  (minor page faults)
                //   Field 12: majflt  (major page faults)
                // The comm field (field 2) is enclosed in parentheses and may contain spaces,
                // so we find the closing ')' first, then split the remainder.
                var stat = File.ReadAllText("/proc/self/stat");
                var commEnd = stat.LastIndexOf(')');

                if (commEnd < 0 || commEnd + 2 >= stat.Length)
                {
                    return;
                }

                var fields = stat[(commEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // After the comm close, fields map to stat(5) positions starting at field 3.
                // minflt = field 10 → index 10 - 3 = 7
                // majflt = field 12 → index 12 - 3 = 9
                if (fields.Length > 9)
                {
                    long.TryParse(fields[7], out minorFaults);
                    long.TryParse(fields[9], out majorFaults);
                }
            }
            catch (IOException)
            {
                // Not available — leave at zero.
            }
        }
    }
}

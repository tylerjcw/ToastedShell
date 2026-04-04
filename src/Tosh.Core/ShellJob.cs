using System.Diagnostics;
using System.Text;

namespace Tosh.Core;

public sealed record ShellJobProcessSpec(
    string ResolvedPath,
    IReadOnlyList<object?> Arguments);

public enum ShellJobRedirectionStream
{
    Output,
    Error,
    OutputThenError,
    ErrorThenOutput,
}

public enum ShellJobRedirectionMode
{
    Truncate,
    Append,
}

public sealed record ShellJobRedirectionSpec(
    string Path,
    ShellJobRedirectionStream Stream,
    ShellJobRedirectionMode Mode);

public enum ShellJobStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

public interface IShellJobDisplayRow
{
    string Kind { get; }
    int? JobId { get; }
    int? ProcessId { get; }
    ShellJobStatus? Status { get; }
    int? ExitCode { get; }
    string Summary { get; }
}

public sealed record ShellJobInfo(
    int Id,
    string Command,
    ShellJobStatus Status,
    int? ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode)
    : IShellJobDisplayRow
{
    public TimeSpan? Duration => (EndedAt ?? DateTimeOffset.Now) - StartedAt;

    string IShellJobDisplayRow.Kind => "job";

    int? IShellJobDisplayRow.JobId => Id;

    ShellJobStatus? IShellJobDisplayRow.Status => Status;

    int? IShellJobDisplayRow.ExitCode => ExitCode;

    string IShellJobDisplayRow.Summary => Command;
}

public sealed record ShellJobCompletion(
    int Id,
    string Command,
    ShellJobStatus Status,
    int? ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int? ExitCode,
    IReadOnlyList<object?> Output,
    IReadOnlyList<string> ErrorLines)
    : IShellJobDisplayRow
{
    public TimeSpan Duration => EndedAt - StartedAt;

    public int OutputCount => Output.Count;

    public int ErrorCount => ErrorLines.Count;

    string IShellJobDisplayRow.Kind => "completion";

    int? IShellJobDisplayRow.JobId => Id;

    ShellJobStatus? IShellJobDisplayRow.Status => Status;

    int? IShellJobDisplayRow.ExitCode => ExitCode;

    string IShellJobDisplayRow.Summary => Command;
}

public sealed record JobControlResult(
    string Action,
    int? JobId,
    int? ProcessId,
    bool IsSuccess,
    string Message) : ICommandResult
    , IShellJobDisplayRow
{
    public ShellJobStatus? Status { get; init; }

    public string Kind => Action;

    public int? ExitCode => null;

    public string Summary => Message;
}

public sealed class ShellJob
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task<ShellJobCompletion>? _completionTask;
    private readonly List<Process> _processes = new();
    private int? _processId;
    private ShellJobStatus _status = ShellJobStatus.Running;
    private DateTimeOffset? _endedAt;
    private int? _exitCode;

    private ShellJob(int id, string command, DateTimeOffset startedAt)
    {
        Id = id;
        Command = command;
        StartedAt = startedAt;
    }

    public int Id { get; }

    public string Command { get; }

    public DateTimeOffset StartedAt { get; }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
            {
                return _processId;
            }
        }
    }

    public ShellJobStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (_sync)
            {
                return _endedAt;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_sync)
            {
                return _exitCode;
            }
        }
    }

    public static ShellJob StartExternalProcess(
        int id,
        string command,
        string resolvedPath,
        string workingDirectory,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<object?>? initialInput = null,
        IReadOnlyList<ShellJobRedirectionSpec>? redirections = null)
    {
        var job = new ShellJob(id, command, DateTimeOffset.Now);
        job.StartPipeline(workingDirectory, [new ShellJobProcessSpec(resolvedPath, arguments)], initialInput, redirections);
        return job;
    }

    public static ShellJob StartExternalPipeline(
        int id,
        string command,
        string workingDirectory,
        IReadOnlyList<ShellJobProcessSpec> stages,
        IReadOnlyList<object?>? initialInput = null,
        IReadOnlyList<ShellJobRedirectionSpec>? redirections = null)
    {
        var job = new ShellJob(id, command, DateTimeOffset.Now);
        job.StartPipeline(workingDirectory, stages, initialInput, redirections);
        return job;
    }

    public ShellJobInfo ToInfo()
    {
        lock (_sync)
        {
            return new ShellJobInfo(Id, Command, _status, _processId, StartedAt, _endedAt, _exitCode);
        }
    }

    public async Task<ShellJobCompletion> WaitAsync(CancellationToken cancellationToken = default)
    {
        var completionTask = _completionTask ?? throw new InvalidOperationException("The background job has not been started.");
        return cancellationToken.CanBeCanceled
            ? await completionTask.WaitAsync(cancellationToken)
            : await completionTask;
    }

    public bool Kill()
    {
        Process[] processes;

        lock (_sync)
        {
            if (_status != ShellJobStatus.Running)
            {
                return false;
            }

            processes = _processes.ToArray();
            _cancellation.Cancel();
        }

        TryKill(processes);
        return true;
    }

    public bool SendSignal(int signal, out string? error)
    {
        Process[] processes;

        lock (_sync)
        {
            if (_status != ShellJobStatus.Running)
            {
                error = "The job is not running.";
                return false;
            }

            processes = _processes.ToArray();
        }

        string? firstError = null;
        var anySent = false;

        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            if (ProcessSignalSender.TrySend(process.Id, signal, out var sendError))
            {
                anySent = true;
                continue;
            }

            firstError ??= sendError;
        }

        error = anySent ? null : firstError ?? "No running process in this job accepted the signal.";
        return anySent;
    }

    private void StartPipeline(
        string workingDirectory,
        IReadOnlyList<ShellJobProcessSpec> stages,
        IReadOnlyList<object?>? initialInput,
        IReadOnlyList<ShellJobRedirectionSpec>? redirections)
    {
        if (stages.Count == 0)
        {
            throw new InvalidOperationException("Background pipelines require at least one external stage.");
        }

        var processes = new List<Process>(stages.Count);

        try
        {
            for (var index = 0; index < stages.Count; index++)
            {
                var stage = stages[index];
                var process = CreateProcess(stage, workingDirectory, redirectInput: index > 0 || (index == 0 && initialInput is { Count: > 0 }));

                if (!process.Start())
                {
                    throw new InvalidOperationException($"Failed to start background job '{Command}'.");
                }

                processes.Add(process);
            }
        }
        catch
        {
            TryKill(processes);

            foreach (var process in processes)
            {
                process.Dispose();
            }

            throw;
        }

        lock (_sync)
        {
            _processes.Clear();
            _processes.AddRange(processes);
            _processId = processes[0].Id;
        }

        _completionTask = MonitorPipelineAsync(processes, initialInput, redirections);
    }

    private static Process CreateProcess(ShellJobProcessSpec stage, string workingDirectory, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = stage.ResolvedPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in stage.Arguments)
        {
            startInfo.ArgumentList.Add(ExternalTextSerializer.SerializeArgument(argument));
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
    }

    private async Task<ShellJobCompletion> MonitorPipelineAsync(
        IReadOnlyList<Process> processes,
        IReadOnlyList<object?>? initialInput,
        IReadOnlyList<ShellJobRedirectionSpec>? redirections)
    {
        var output = new List<object?>();
        var errorLines = new List<string>();
        var errorLock = new object();
        var finalProcess = processes[^1];
        var bufferedPlans = CreateBufferedRedirectionPlans(redirections);
        var outputWriters = new List<TextWriter>();
        var errorWriters = new List<TextWriter>();
        var disposableWriters = new List<TextWriter>();

        if (redirections is { Count: > 0 })
        {
            foreach (var plan in bufferedPlans.Values)
            {
                if (plan.HasOutput)
                {
                    outputWriters.Add(plan.OutputWriter);
                }

                if (plan.HasError)
                {
                    errorWriters.Add(plan.ErrorWriter);
                }
            }

            foreach (var redirection in redirections)
            {
                if (bufferedPlans.ContainsKey(redirection.Path))
                {
                    continue;
                }

                var mode = redirection.Mode == ShellJobRedirectionMode.Append ? FileMode.Append : FileMode.Create;
                var writer = TextWriter.Synchronized(new StreamWriter(File.Open(redirection.Path, mode, FileAccess.Write, FileShare.Read), Encoding.UTF8));
                disposableWriters.Add(writer);

                if (RedirectionIncludesOutput(redirection.Stream))
                {
                    outputWriters.Add(writer);
                }

                if (RedirectionIncludesError(redirection.Stream))
                {
                    errorWriters.Add(writer);
                }
            }
        }

        using var cancellationRegistration = _cancellation.Token.Register(() => TryKill(processes));
        var stderrTasks = processes
            .Select(process => PumpStandardErrorAsync(process, errorLines, errorLock, errorWriters))
            .ToArray();
        var pipeTasks = new List<Task>();
        Task? stdinTask = null;

        if (initialInput is { Count: > 0 })
        {
            stdinTask = PumpStandardInputAsync(processes[0], initialInput, _cancellation.Token);
        }

        for (var index = 0; index < processes.Count - 1; index++)
        {
            pipeTasks.Add(PumpPipeAsync(processes[index], processes[index + 1], _cancellation.Token));
        }

        try
        {
            while (true)
            {
                string? line;

                try
                {
                    line = await finalProcess.StandardOutput.ReadLineAsync(_cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (outputWriters.Count > 0)
                {
                    foreach (var writer in outputWriters)
                    {
                        await writer.WriteLineAsync(line);
                        await writer.FlushAsync(_cancellation.Token);
                    }
                }
                else
                {
                    output.Add(new ShellTextLine(line));
                }
            }
        }
        finally
        {
            if (stdinTask is not null)
            {
                await AwaitAndIgnoreClosedPipeAsync(stdinTask);
            }

            try
            {
                await Task.WhenAll(pipeTasks);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }

            try
            {
                await Task.WhenAll(stderrTasks);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }

            await FlushBufferedRedirectionsAsync(bufferedPlans.Values, _cancellation.Token);

            foreach (var writer in disposableWriters)
            {
                await writer.DisposeAsync();
            }

            foreach (var process in processes)
            {
                try
                {
                    await process.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        var endedAt = DateTimeOffset.Now;
        var cancelled = _cancellation.IsCancellationRequested;
        var exitCodes = processes
            .Select(TryGetExitCode)
            .ToArray();
        var lastExitCode = exitCodes.LastOrDefault();
        var status = cancelled
            ? ShellJobStatus.Cancelled
            : exitCodes.All(code => code == 0)
                ? ShellJobStatus.Completed
                : ShellJobStatus.Failed;

        lock (_sync)
        {
            _status = status;
            _endedAt = endedAt;
            _exitCode = lastExitCode;
        }

        foreach (var process in processes)
        {
            process.Dispose();
        }

        return new ShellJobCompletion(
            Id,
            Command,
            status,
            ProcessId,
            StartedAt,
            endedAt,
            lastExitCode,
            output.ToArray(),
            errorLines.ToArray());
    }

    private static async Task PumpStandardInputAsync(Process process, IReadOnlyList<object?> input, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var item in input)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await process.StandardInput.WriteLineAsync(ExternalTextSerializer.Serialize(item));
            }
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }
        }
    }

    private static async Task PumpPipeAsync(Process source, Process destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.StandardOutput.BaseStream.CopyToAsync(destination.StandardInput.BaseStream, cancellationToken);
            await destination.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                destination.StandardInput.Close();
            }
            catch
            {
            }
        }
    }

    private static async Task PumpStandardErrorAsync(
        Process process,
        ICollection<string> errorLines,
        object sync,
        IReadOnlyList<TextWriter> errorWriters)
    {
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync();

            if (line is null)
            {
                break;
            }

            if (errorWriters.Count > 0)
            {
                foreach (var writer in errorWriters)
                {
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync();
                }
            }
            else
            {
                lock (sync)
                {
                    errorLines.Add(line);
                }
            }
        }
    }

    private static bool RedirectionIncludesOutput(ShellJobRedirectionStream stream)
        => stream is ShellJobRedirectionStream.Output or ShellJobRedirectionStream.OutputThenError or ShellJobRedirectionStream.ErrorThenOutput;

    private static bool RedirectionIncludesError(ShellJobRedirectionStream stream)
        => stream is ShellJobRedirectionStream.Error or ShellJobRedirectionStream.OutputThenError or ShellJobRedirectionStream.ErrorThenOutput;

    private static Dictionary<string, BufferedShellJobRedirectionPlan> CreateBufferedRedirectionPlans(
        IReadOnlyList<ShellJobRedirectionSpec>? redirections)
    {
        if (redirections is null or { Count: 0 })
        {
            return new Dictionary<string, BufferedShellJobRedirectionPlan>(StringComparer.OrdinalIgnoreCase);
        }

        return redirections
            .GroupBy(static redirection => redirection.Path, StringComparer.OrdinalIgnoreCase)
            .Where(static group =>
                group.Count() > 1 ||
                group.Any(static redirection => redirection.Stream is ShellJobRedirectionStream.OutputThenError or ShellJobRedirectionStream.ErrorThenOutput))
            .ToDictionary(
                static group => group.Key,
                static group => new BufferedShellJobRedirectionPlan(group.Key, group.ToArray()),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task FlushBufferedRedirectionsAsync(
        IEnumerable<BufferedShellJobRedirectionPlan> plans,
        CancellationToken cancellationToken)
    {
        foreach (var plan in plans)
        {
            var outputText = plan.OutputBuffer.ToString();
            var errorText = plan.ErrorBuffer.ToString();

            foreach (var redirection in plan.Redirections)
            {
                var text = GetRedirectionContent(redirection.Stream, outputText, errorText);
                var fileMode = redirection.Mode == ShellJobRedirectionMode.Append ? FileMode.Append : FileMode.Create;
                await using var writer = new StreamWriter(File.Open(redirection.Path, fileMode, FileAccess.Write, FileShare.Read), Encoding.UTF8);

                if (text.Length > 0)
                {
                    await writer.WriteAsync(text.AsMemory(), cancellationToken);
                }

                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private static string GetRedirectionContent(
        ShellJobRedirectionStream stream,
        string outputText,
        string errorText)
        => stream switch
        {
            ShellJobRedirectionStream.Output => outputText,
            ShellJobRedirectionStream.Error => errorText,
            ShellJobRedirectionStream.OutputThenError => outputText + errorText,
            ShellJobRedirectionStream.ErrorThenOutput => outputText + errorText,
            _ => string.Empty,
        };

    private sealed class BufferedShellJobRedirectionPlan
    {
        public BufferedShellJobRedirectionPlan(
            string path,
            IReadOnlyList<ShellJobRedirectionSpec> redirections)
        {
            Path = path;
            Redirections = redirections;
            OutputWriter = TextWriter.Synchronized(new StringWriter(OutputBuffer));
            ErrorWriter = TextWriter.Synchronized(new StringWriter(ErrorBuffer));
            HasOutput = redirections.Any(static redirection => RedirectionIncludesOutput(redirection.Stream));
            HasError = redirections.Any(static redirection => RedirectionIncludesError(redirection.Stream));
        }

        public string Path { get; }

        public IReadOnlyList<ShellJobRedirectionSpec> Redirections { get; }

        public StringBuilder OutputBuffer { get; } = new();

        public StringBuilder ErrorBuffer { get; } = new();

        public TextWriter OutputWriter { get; }

        public TextWriter ErrorWriter { get; }

        public bool HasOutput { get; }

        public bool HasError { get; }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static async Task AwaitAndIgnoreClosedPipeAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryKill(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                try
                {
                    Console.Error.WriteLine($"tosh: warning: failed to kill process {process.Id}: {ex.Message}");
                }
                catch
                {
                    Console.Error.WriteLine($"tosh: warning: failed to kill process: {ex.Message}");
                }
            }
            catch
            {
            }
        }
    }
}

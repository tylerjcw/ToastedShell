using System.Diagnostics;

namespace Tosh.Core.Commands;

public sealed class ExternalProcessCommand : IShellCommand, ICommandResolutionMetadata, IImplicitGlobCommand
{
    private readonly string _resolvedPath;

    public ExternalProcessCommand(string name, string resolvedPath)
    {
        Name = name;
        _resolvedPath = resolvedPath;
    }

    public string Name { get; }

    public string ResolvedPath => _resolvedPath;

    public string Description => $"Executes the external program at '{_resolvedPath}'.";

    public string Usage => $"{Name} [arg ...]";

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.External;

    public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (ShouldUseTerminalPassthrough(context))
        {
            await ExecuteWithTerminalPassthroughAsync(context);
            yield break;
        }

        await foreach (var item in ExecuteWithPipesAsync(context))
        {
            yield return item;
        }
    }

    private bool ShouldUseTerminalPassthrough(CommandContext context)
    {
        return !context.IsPipelined
               && !Console.IsInputRedirected
               && !Console.IsOutputRedirected
               && ReferenceEquals(context.Runtime.Output, Console.Out)
               && ReferenceEquals(context.Runtime.Error, Console.Error);
    }

    private async Task ExecuteWithTerminalPassthroughAsync(CommandContext context)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _resolvedPath,
            WorkingDirectory = context.Runtime.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        foreach (var argument in context.Arguments)
        {
            startInfo.ArgumentList.Add(ExternalTextSerializer.SerializeArgument(argument));
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var cancellationRegistration = context.CancellationToken.Register(() => TryKill(process));

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external command '{Name}'.");
        }

        await process.WaitForExitAsync(context.CancellationToken);
        context.Runtime.SetLastExitCode(process.ExitCode);
        context.PipelineExitStatusTracker?.Record(process.ExitCode);
    }

    private async IAsyncEnumerable<object?> ExecuteWithPipesAsync(
        CommandContext context)
    {
        using var process = CreatePipedProcess(context);
        using var cancellationRegistration = context.CancellationToken.Register(() => TryKill(process));

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external command '{Name}'.");
        }

        var stderrTask = PumpStandardErrorAsync(process, context.Runtime.Error, context.CancellationToken);
        var stdinTask = PumpStandardInputAsync(process, context.Input, context.CancellationToken);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(context.CancellationToken);

                if (line is null)
                {
                    break;
                }

                yield return new ShellTextLine(line);
            }
        }
        finally
        {
            await AwaitAndIgnoreClosedPipeAsync(stdinTask);
            await AwaitAndIgnoreClosedPipeAsync(stderrTask);
            await process.WaitForExitAsync(CancellationToken.None);
            context.Runtime.SetLastExitCode(process.ExitCode);
            context.PipelineExitStatusTracker?.Record(process.ExitCode);
        }
    }

    private Process CreatePipedProcess(CommandContext context)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _resolvedPath,
            WorkingDirectory = context.Runtime.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in context.Arguments)
        {
            startInfo.ArgumentList.Add(ExternalTextSerializer.SerializeArgument(argument));
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
    }

    private static async Task PumpStandardErrorAsync(Process process, TextWriter errorWriter, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                break;
            }

            await errorWriter.WriteLineAsync(line);
        }
    }

    private static async Task PumpStandardInputAsync(Process process, IAsyncEnumerable<object?> input, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in input.WithCancellation(cancellationToken))
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

    private static async Task AwaitAndIgnoreClosedPipeAsync(Task task)
    {
        try
        {
            await task;
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
        catch (OperationCanceledException)
        {
        }
    }

    private static void TryKill(Process process)
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

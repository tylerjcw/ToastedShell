using System.Diagnostics;

namespace Tosh.Core.Commands;

public sealed class ExternalProcessCommand : IShellCommand, ICommandResolutionMetadata
{
    private readonly string _resolvedPath;

    public ExternalProcessCommand(string name, string resolvedPath)
    {
        Name = name;
        _resolvedPath = resolvedPath;
    }

    public string Name { get; }

    public string Description => $"Executes the external program at '{_resolvedPath}'.";

    public string Usage => $"{Name} [arg ...]";

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.External;

    public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        using var process = CreateProcess(context);
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
            await stderrTask;
            await process.WaitForExitAsync(context.CancellationToken);
            context.Runtime.SetLastExitCode(process.ExitCode);
        }
    }

    private Process CreateProcess(CommandContext context)
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
            startInfo.ArgumentList.Add(ExternalTextSerializer.Serialize(argument));
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
        catch
        {
        }
    }
}

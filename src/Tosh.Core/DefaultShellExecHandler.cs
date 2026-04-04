using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tosh.Core;

internal sealed class DefaultShellExecHandler : IShellExecHandler
{
    public async Task<ShellExecResult> ExecuteAsync(ShellExecRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            ExecuteWithExecVp(request);
            throw new InvalidOperationException("exec unexpectedly returned after attempting to replace the current process.");
        }

        return await ExecuteWithFallbackProcessAsync(request, cancellationToken);
    }

    private static void ExecuteWithExecVp(ShellExecRequest request)
    {
        var originalDirectory = Environment.CurrentDirectory;
        IntPtr argvPointer = IntPtr.Zero;
        IntPtr[]? argvEntries = null;

        try
        {
            Directory.SetCurrentDirectory(request.WorkingDirectory);
            argvEntries = BuildArgv(request);
            argvPointer = Marshal.AllocHGlobal(IntPtr.Size * argvEntries.Length);
            Marshal.Copy(argvEntries, 0, argvPointer, argvEntries.Length);

            Interop.execvp(request.ExecutablePath, argvPointer);

            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Failed to replace the current process with '{request.ExecutablePath}'.",
                new Win32Exception(error));
        }
        finally
        {
            if (argvEntries is not null)
            {
                foreach (var entry in argvEntries)
                {
                    if (entry != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(entry);
                    }
                }
            }

            if (argvPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(argvPointer);
            }

            if (!string.Equals(Environment.CurrentDirectory, originalDirectory, StringComparison.Ordinal))
            {
                Directory.SetCurrentDirectory(originalDirectory);
            }
        }
    }

    private static IntPtr[] BuildArgv(ShellExecRequest request)
    {
        var argvEntries = new IntPtr[request.Arguments.Count + 2];
        argvEntries[0] = Marshal.StringToCoTaskMemUTF8(request.ExecutablePath);

        for (var index = 0; index < request.Arguments.Count; index++)
        {
            argvEntries[index + 1] = Marshal.StringToCoTaskMemUTF8(request.Arguments[index]);
        }

        argvEntries[^1] = IntPtr.Zero;
        return argvEntries;
    }

    private static async Task<ShellExecResult> ExecuteWithFallbackProcessAsync(
        ShellExecRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external command '{request.ExecutablePath}'.");
        }

        await process.WaitForExitAsync(cancellationToken);
        return new ShellExecResult(ReplacedCurrentProcess: false, ExitCode: process.ExitCode);
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

    private static class Interop
    {
        [DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
        internal static extern int execvp(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
            IntPtr argv);
    }
}

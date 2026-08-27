using System.Reflection;

namespace Tosh.Runtime;

/// <summary>
/// Converts an exception escaping a compiled Tōast executable into the same
/// diagnostic surface used by the interactive runtime.
/// </summary>
/// <remarks>
/// The boundary deliberately stays inactive when an emitted <c>Main</c> is
/// invoked by an embedding host or through reflection. In that case the host
/// owns exception handling and must receive the original exception unchanged.
/// </remarks>
public static class CompiledProgramBoundary
{
    /// <summary>
    /// Reports <paramref name="exception"/> when <paramref name="programAssembly"/>
    /// is the process entry assembly, and returns whether it was handled.
    /// </summary>
    public static bool TryReportUnhandledException(
        Exception exception,
        Assembly programAssembly)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(programAssembly);

        if (!ReferenceEquals(Assembly.GetEntryAssembly(), programAssembly))
        {
            return false;
        }

        var renderer = new DiagnosticRenderer(
            theme: null,
            config: null,
            forcePlain: Console.IsErrorRedirected);
        Console.Error.WriteLine(renderer.Render(exception));
        Environment.ExitCode = 1;
        return true;
    }
}

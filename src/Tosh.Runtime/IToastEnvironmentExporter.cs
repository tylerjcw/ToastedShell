namespace Tosh.Runtime;

/// <summary>
/// Lets a host track a process-environment assignment made through <c>$env</c>.
/// </summary>
/// <remarks>
/// An embedded Tōast runtime can write the process environment directly. TōSh also
/// records the name as exported and mirrors the value into its session variables, so that
/// bookkeeping is an optional host capability rather than a reason for the language to
/// hold a <see cref="ToshRuntime"/> (`TOAST-0006`).
/// </remarks>
public interface IToastEnvironmentExporter
{
    void ExportEnvironmentVariable(string name, object? value);
}

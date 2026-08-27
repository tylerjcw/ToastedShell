namespace Tosh.Runtime;

/// <summary>
/// A command that runs a program on disk rather than executing in-process.
/// </summary>
/// <remarks>
/// <para>
/// Exists so the language can ask "is this stage an external program, and where does it
/// live?" without naming the shell's implementation of one. That question has exactly
/// one caller — the background-job path, which can only build a pipeline of OS
/// processes — and it previously asked it by testing for
/// <c>Tosh.Stdlib.ExternalProcessCommand</c>, which is what made <c>Tosh.Language</c>
/// depend on the shell's command library (`TOAST-0004`).
/// </para>
/// <para>
/// Deliberately narrow. The only thing the language needs beyond the command contract
/// is the resolved path, because that is what a job specification is built from;
/// everything else about launching a process stays on the shell side.
/// </para>
/// </remarks>
public interface IExternalProcessCommand : IShellCommand
{
    /// <summary>
    /// Absolute path to the program this command launches. Resolved when the command
    /// was created, so it does not change with the working directory or `PATH`.
    /// </summary>
    string ResolvedPath { get; }
}

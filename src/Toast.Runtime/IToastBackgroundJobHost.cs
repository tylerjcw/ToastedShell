namespace Tosh.Runtime;

/// <summary>A process stage requested by a Tōast background pipeline.</summary>
public sealed record ToastBackgroundProcessSpec(
    string ResolvedPath,
    IReadOnlyList<object?> Arguments);

/// <summary>The stream or stream ordering targeted by a background redirection.</summary>
public enum ToastBackgroundRedirectionStream
{
    Output,
    Error,
    OutputThenError,
    ErrorThenOutput,
}

/// <summary>Whether a background redirection replaces or extends its target.</summary>
public enum ToastBackgroundRedirectionMode
{
    Truncate,
    Append,
}

/// <summary>A file destination requested by a Tōast background pipeline.</summary>
public sealed record ToastBackgroundRedirectionSpec(
    string Path,
    ToastBackgroundRedirectionStream Stream,
    ToastBackgroundRedirectionMode Mode);

/// <summary>
/// Everything a host needs to launch a background pipeline without exposing its job model
/// to the language.
/// </summary>
/// <param name="InputPath">
/// The <c>in&lt;</c> file, whose bytes become the first program's stdin unchanged. Handed over
/// as a path rather than read into <paramref name="InitialInput"/> as lines, which decoded the
/// bytes and so corrupted anything that was not text (<c>TOSH-0012</c>).
/// </param>
public sealed record ToastBackgroundPipelineRequest(
    string CommandText,
    string WorkingDirectory,
    IReadOnlyList<ToastBackgroundProcessSpec> Stages,
    IReadOnlyList<object?>? InitialInput,
    IReadOnlyList<ToastBackgroundRedirectionSpec> Redirections,
    string? InputPath = null);

/// <summary>
/// Starts and tracks background process pipelines on behalf of the language.
/// </summary>
/// <remarks>
/// Backgrounding is Tōast syntax, but process ownership, job identifiers and the job
/// table belong to its host. This port keeps <c>ShellJob</c> and its related types out of
/// <c>Tosh.Language</c> while allowing TōSh to retain its existing job-control model
/// (`TOAST-0006`).
/// </remarks>
public interface IToastBackgroundJobHost
{
    /// <summary>
    /// Starts and registers the requested pipeline, returning the host-defined value that
    /// should become the session's last result.
    /// </summary>
    object StartExternalPipeline(ToastBackgroundPipelineRequest request);
}

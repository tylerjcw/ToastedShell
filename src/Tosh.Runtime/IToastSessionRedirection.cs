namespace Tosh.Runtime;

/// <summary>
/// Mirrors a language redirection into an optional host session.
/// </summary>
/// <remarks>
/// Tōast owns redirection and its <see cref="IToastStream"/> destinations. A shell may
/// also need its legacy session writers redirected while a pipeline runs so shell-provided
/// commands and external processes observe the same destinations. The language asks for a
/// scope through this port without knowing whether a session exists (`TOAST-0006`).
/// </remarks>
public interface IToastSessionRedirection
{
    /// <summary>
    /// Redirects the host session until the returned scope is disposed. A
    /// <see langword="null"/> destination leaves that side of the session unchanged.
    /// </summary>
    IDisposable Begin(IToastStream? output, IToastStream? error);
}

namespace Tosh.Runtime;

/// <summary>
/// Observes execution state a host may retain without making that session state part of
/// Tōast evaluation.
/// </summary>
/// <remarks>
/// TōSh uses these notifications to back <c>$tosh.Last.Result</c> and
/// <c>$tosh.Last.ExitCode</c>. An embedded language host may ignore them entirely.
/// </remarks>
public interface IToastExecutionObserver
{
    void SetLastResult(object? value);

    void SetLastExitCode(int exitCode);
}

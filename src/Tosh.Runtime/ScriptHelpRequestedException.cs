namespace Tosh.Runtime;

/// <summary>
/// Thrown once a script's usage has been written in response to <c>--help</c>, to unwind out of
/// the script without running its body.
/// </summary>
/// <remarks>
/// <para>
/// A signal rather than an error: nothing went wrong, and the host is expected to catch it and
/// exit successfully. It exists because <c>exit</c> does not stop a running script — it records an
/// exit code and execution carries on — so there was no way to answer <c>--help</c> and then
/// decline to do the work.
/// </para>
/// </remarks>
public sealed class ScriptHelpRequestedException : Exception
{
    public ScriptHelpRequestedException()
        : base("Script usage was requested with '--help'.")
    {
    }
}

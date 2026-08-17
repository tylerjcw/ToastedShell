namespace Tosh.Runtime;

/// <summary>
/// Session-level facts the language observes, and the one it asks for, in a host that
/// has a session at all.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0006`, stage 2b. Both of these describe *shell* state — whether the session is
/// winding down, and whether a name has been exported to the environment — yet the
/// language needs them. Naming them as host capabilities keeps that state out of Tōast
/// while leaving the language able to ask.
/// </para>
/// <para>
/// **The membership here is smaller than expected, and the reason is worth recording.**
/// The plan described the language as *setting* `ExitRequested` and marking exports. It
/// does neither in the way that suggests:
/// </para>
/// <list type="bullet">
/// <item>
/// `ExitRequested` has a private setter. The language reads it in four places — every
/// one a loop-stop condition — and requests exit in exactly one, the `--help` path for a
/// script's declared parameters, where the question has been answered so the body must
/// not run (`TS-P2-52`).
/// </item>
/// <item>
/// Exports are already shell-side and were before this item: `ExportCommand` calls
/// `ToshRuntime.ExportEnvironmentVariable`. The language's entire involvement is one
/// membership test, in `forget`, to report whether the name it removed had been
/// exported.
/// </item>
/// </list>
/// <para>
/// So this is mostly an *observation* port with a single signal on it, which is why an
/// embedded host can implement `RequestExit` as a no-op: nothing in the language depends
/// on the request being honoured, only on being able to make it.
/// </para>
/// </remarks>
public interface IToastHostSignals
{
    /// <summary>
    /// Whether the session has been asked to stop. Statement loops check this between
    /// statements, so an `exit` takes effect at the next statement boundary rather than
    /// unwinding through an exception.
    /// </summary>
    bool ExitRequested { get; }

    /// <summary>
    /// Asks the session to stop. A host with no session may ignore this.
    /// </summary>
    void RequestExit();

    /// <summary>
    /// Whether <paramref name="name"/> has been exported to the process environment.
    /// Read by `forget`, which reports what it removed.
    /// </summary>
    bool IsExported(string name);
}

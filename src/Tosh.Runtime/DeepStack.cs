namespace Tosh.Runtime;

/// <summary>
/// Reports the stack size ToastScript's evaluator threads run on — `TOAST-0049`.
/// </summary>
/// <remarks>
/// <para>
/// ToastScript's recursion ceiling is a stack-size question, not an arbitrary one. Each
/// Tōast frame costs a chain of async state-machine frames, and on the usual 8 MB stack the
/// evaluator aborts between depth 250 and 300 — roughly 28 KB of CLR stack per Tōast frame.
/// That is what limited a recursive-descent parser to about forty nested parentheses.
/// </para>
/// <para>
/// Two levers were measured before this one, and both were rejected:
/// </para>
/// <list type="bullet">
///   <item>
///     A single thread created with a large explicit stack does nothing on its own: the
///     evaluator's `await`s suspend, their continuations are posted to the thread pool, and
///     the recursion resumes on a pool thread with an ordinary stack. Measured — a 64 MB
///     thread left the wall exactly where it was, still aborting at depth 300.
///   </item>
///   <item>
///     Pumping those continuations back onto that one thread does work — it reached depth
///     8,900 — but it makes every `await` in the engine single-threaded, and the engine
///     bridges sync to async in 23 places (list comprehensions among them) by blocking on
///     `GetAwaiter().GetResult()`. A blocking call on the pump thread whose continuation is
///     queued behind it is a deadlock, and a hang in a login shell is a worse failure than
///     a shallow stack. It also cost about 10% throughput.
///   </item>
/// </list>
/// <para>
/// What is left is the CLR's own setting, which gives *every* thread the larger stack —
/// main, pool, and any created later — so there is no thread the recursion can hop onto
/// that has the small one, and no scheduling behaviour changes at all. It costs nothing:
/// measured startup was 286 ms with it against 330 ms without, and a compute loop 726 ms
/// against 740 ms. It has to be set before the process starts, so this class reads it and
/// the guard sizes itself accordingly, rather than the limit being a number nobody can
/// change.
/// </para>
/// <para>
/// The recursion guard is still what reports a limit. This only decides how much stack there
/// is to be guarded, so <see cref="ToshExecutionDepthGuard.MaximumSafeDepth"/> can be set
/// from a measurement rather than from the smallest stack anyone might have.
/// </para>
/// </remarks>
public static class DeepStack
{
    /// <summary>
    /// The stack size a thread gets when nothing has been configured.
    /// </summary>
    /// <remarks>
    /// The Linux default, and what <see cref="ToshExecutionDepthGuard.MaximumSafeDepth"/>
    /// is measured against.
    /// </remarks>
    public const ulong DefaultStackBytes = 8UL * 1024 * 1024;

    /// <summary>
    /// The environment variable the CLR reads a thread's default stack size from.
    /// </summary>
    /// <remarks>
    /// It is read once, at CLR start-up, before any managed code runs — which is why this
    /// only reports the setting rather than applying it. Two in-process alternatives were
    /// measured and neither works: `setrlimit(RLIMIT_STACK)` from `Main` is too late,
    /// because the runtime has already cached the default; and a `runtimeconfig.json`
    /// `configProperties` entry of the same name is ignored entirely.
    /// </remarks>
    public const string StackSizeVariable = "DOTNET_Thread_DefaultStackSize";

    /// <summary>
    /// The stack size threads in this process are created with.
    /// </summary>
    /// <remarks>
    /// The value is hexadecimal without a prefix, which is how the CLR itself parses it —
    /// `0x4000000` and `4000000` both mean 64 MB. Anything unparseable means the setting is
    /// not in effect, so the default is the honest answer.
    /// </remarks>
    public static ulong ThreadStackBytes()
    {
        var configured = Environment.GetEnvironmentVariable(StackSizeVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultStackBytes;
        }

        var text = configured.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var bytes) && bytes > 0
            ? bytes
            : DefaultStackBytes;
    }
}

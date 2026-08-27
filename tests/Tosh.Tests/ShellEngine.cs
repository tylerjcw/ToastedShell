using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// Constructs an engine carrying the complete TōSh command set — <c>TOAST-0006</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>new ToshEngine(ToshRuntime.CreateDefault().Language)</c> means "the whole shell" only because the compatibility
/// constructor builds a <see cref="ToshRuntime"/> when it is given none. That is the
/// language assembly quietly loading the shell, and it is what the assembly division has
/// to remove — but hundreds of tests depend on the meaning rather than on the spelling.
/// </para>
/// <para>
/// So the meaning gets a name. A test that wants the full command set asks for it here,
/// and the language keeps no constructor that supplies a shell nobody asked for. Tests
/// wanting the *language* still construct from a <see cref="ToastRuntime"/> directly —
/// that is the distinction this factory exists to make visible.
/// </para>
/// </remarks>
internal static class ShellEngine
{
    /// <summary>An engine with a fresh TōSh session runtime and its full command set.</summary>
    public static ToshEngine CreateFullShell() => new(ToshRuntime.CreateDefault().Language);

    /// <summary>An engine over an existing TōSh session runtime.</summary>
    public static ToshEngine CreateFullShell(ToshRuntime runtime) => new(runtime.Language);

    /// <summary>
    /// The TōSh session runtime behind an engine — <c>TOAST-0006</c>.
    /// </summary>
    /// <remarks>
    /// Replaces <c>ToshEngine.Runtime</c>, which typed a shell class on a language type. The
    /// engine carries its host opaquely on the language runtime; this recovers it, from the
    /// test project, for the assertions that genuinely want shell state — display, config,
    /// the job table, the concrete command registry.
    /// </remarks>
    public static ToshRuntime Shell(this ToshEngine engine)
        => engine.LanguageRuntime.CommandHost as ToshRuntime
           ?? throw new InvalidOperationException(
               "This engine has no TōSh session runtime; it was built from a language-only ToastRuntime.");
}

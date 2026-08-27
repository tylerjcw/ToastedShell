namespace Tosh.Runtime;

/// <summary>
/// Recovers the TōSh session runtime a shell command needs — <c>TOAST-0006</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CommandContext"/> lives on the language side and named <c>ToshRuntime</c>
/// directly, which is the coupling that keeps a language type tied to the shell assembly.
/// The host seam already exists: the language runtime carries an opaque
/// <c>IToastCommandHost</c>, and TōSh supplies itself as one.
/// </para>
/// <para>
/// So a shell command asks for its host by name, from the shell's own assembly. This is
/// declared in the <c>Tosh.Runtime</c> namespace rather than <c>Tosh.Stdlib</c> because
/// every command file already imports it for <see cref="CommandContext"/> — the extension
/// resolves without touching 320 files' usings, and the dependency it expresses is named in
/// exactly one place instead of being spelled out at each call.
/// </para>
/// </remarks>
public static class ShellCommandContextExtensions
{
    /// <summary>The TōSh session runtime hosting this command.</summary>
    /// <exception cref="InvalidOperationException">
    /// The active Tōast runtime supplied no TōSh host — the command needs a shell capability
    /// the embedding host does not provide.
    /// </exception>
    public static ToshRuntime Shell(this CommandContext context)
        => context.RequireCommandHost<ToshRuntime>();
}

namespace Tosh.Runtime;

/// <summary>
/// Creates the command object that launches a program on disk.
/// </summary>
/// <remarks>
/// <para>
/// The inversion behind `TOAST-0004`. Name resolution belongs to the language — it is
/// the language that decides a bare word is neither a variable, a function, a class nor
/// a builtin, and must therefore be a program — but *launching* a process is the
/// shell's business. Constructing the shell's command type directly was the second and
/// last thing tying <c>Tosh.Language</c> to <c>Tosh.Stdlib</c>.
/// </para>
/// <para>
/// A host may leave this unset. Embedding Tōast to evaluate expressions, with no
/// intention of spawning processes, is a legitimate configuration; a script that then
/// invokes an external program gets a diagnostic saying so rather than a null
/// reference. That is the whole reason this is a registered capability and not an
/// assumed one.
/// </para>
/// <para>
/// Registered the same way the command set is — see
/// <see cref="ToshRuntime.DefaultCommandRegistrar"/>, which <c>Tosh.Stdlib</c> installs
/// from a module initializer.
/// </para>
/// </remarks>
public interface IExternalCommandFactory
{
    /// <summary>
    /// Creates a command that launches <paramref name="resolvedPath"/>.
    /// </summary>
    /// <param name="name">The name as written in the script, kept for diagnostics and help.</param>
    /// <param name="resolvedPath">Absolute path to the program, already resolved and verified executable.</param>
    IExternalProcessCommand CreateExternalProcess(string name, string resolvedPath);
}

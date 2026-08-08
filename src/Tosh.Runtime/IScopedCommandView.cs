namespace Tosh.Runtime;

/// <summary>
/// The commands visible from wherever a command is running — lexical scopes as well as the
/// global registry.
/// </summary>
/// <remarks>
/// <para>
/// A <c>func</c> declared without <c>global</c> or <c>export</c> registers in the innermost
/// lexical scope, not in <see cref="ShellCommandRegistry"/>. Running a script pushes a scope, so
/// every function a script declares lands there — callable on the next line, and invisible to
/// anything that introspects the registry. <c>help fn</c> answered "topic not found" and
/// <c>which fn</c> answered nothing at all, for a function the same script had just called
/// (<c>TS-P2-54</c>). At the <c>-c</c> prompt there is no scope to land in, which is why the
/// same script worked when pasted rather than run.
/// </para>
/// <para>
/// This is the command-side twin of <see cref="ITypeResolver"/>, which
/// <see cref="CommandContext.ScopedTypeResolver"/> already carries for exactly the same reason.
/// <see cref="ShellCommandRegistry"/> implements it, so a caller with no lexical scope passes
/// the registry itself rather than a wrapper.
/// </para>
/// </remarks>
public interface IScopedCommandView
{
    /// <summary>
    /// Every visible command, each name resolved once — a nearer scope shadows a further one,
    /// and both shadow the global registry.
    /// </summary>
    IEnumerable<IShellCommand> All { get; }

    /// <summary>Resolves one name, nearest scope first.</summary>
    bool TryGet(string name, out IShellCommand command);

    /// <summary>Alias names that resolve to each canonical command name.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> GetAliasMap();
}

using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// The engine's answer to "which commands can be seen from here": its lexical scopes, innermost
/// first, falling through to the global registry.
/// </summary>
/// <remarks>
/// <para>
/// Built once per command invocation from a snapshot of the scope stack, so a command that runs
/// long — or asynchronously — reports the scopes it was called from rather than whatever the
/// engine has pushed since.
/// </para>
/// <para>
/// Shadowing follows lookup: the first scope declaring a name wins, and the registry is asked
/// last. That is the order <c>ResolveCommand</c> already uses, so <c>help fn</c> describes the
/// same <c>fn</c> that calling <c>fn</c> would run.
/// </para>
/// </remarks>
internal sealed class ScopedCommandView : IScopedCommandView
{
    private readonly IReadOnlyList<LexicalScope> _scopes;
    private readonly ShellCommandRegistry _registry;

    internal ScopedCommandView(IReadOnlyList<LexicalScope> scopes, ShellCommandRegistry registry)
    {
        _scopes = scopes;
        _registry = registry;
    }

    public IEnumerable<IShellCommand> All
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var scope in _scopes)
            {
                foreach (var (name, command) in scope.Commands)
                {
                    if (seen.Add(name))
                    {
                        yield return command;
                    }
                }
            }

            foreach (var command in _registry.All)
            {
                if (seen.Add(command.Name))
                {
                    yield return command;
                }
            }
        }
    }

    public bool TryGet(string name, out IShellCommand command)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Commands.TryGetValue(name, out var scoped))
            {
                command = scoped;
                return true;
            }
        }

        return _registry.TryGet(name, out command);
    }

    /// <summary>
    /// Aliases come from the registry alone: a scope's command table stores each name as its own
    /// entry, so a scoped alias is already a command in <see cref="All"/> rather than an alias of
    /// one.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetAliasMap() => _registry.GetAliasMap();
}

using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// The engine's answer to "which commands can be seen from here": its lexical scopes, innermost
/// first, then the global registry, then the exports of every visible module.
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
/// <para>
/// `TS-P2-68`. Module exports come last, and they are the reason this class knows about the
/// engine: a module's commands live in its own export table, which is neither a scope nor the
/// registry, so <c>ToastLib.Filesystem.GetFileName</c> was callable and invisible at once. The
/// walk is the engine's own, not a second one written here.
/// </para>
/// </remarks>
internal sealed class ScopedCommandView : IScopedCommandView
{
    private readonly IReadOnlyList<LexicalScope> _scopes;
    private readonly ICommandTable _registry;
    private readonly IReadOnlyList<ShellModuleSummary> _modules;
    private readonly ToshEngine _engine;
    private IReadOnlyList<KeyValuePair<string, IShellCommand>>? _qualified;

    internal ScopedCommandView(
        IReadOnlyList<LexicalScope> scopes,
        ICommandTable registry,
        IReadOnlyList<ShellModuleSummary> modules,
        ToshEngine engine)
    {
        _scopes = scopes;
        _registry = registry;
        _modules = modules;
        _engine = engine;
    }

    public IReadOnlyList<ShellModuleSummary> Modules => _modules;

    public IReadOnlyList<KeyValuePair<string, IShellCommand>> QualifiedCommands =>
        _qualified ??= BuildQualifiedCommands();

    private IReadOnlyList<KeyValuePair<string, IShellCommand>> BuildQualifiedCommands()
    {
        var resolved = new List<KeyValuePair<string, IShellCommand>>();

        foreach (var module in _modules)
        {
            foreach (var name in module.Commands)
            {
                var qualified = $"{module.QualifiedName}.{name}";

                if (_engine.TryGetModuleCommandByQualifiedName(qualified, out var command))
                {
                    resolved.Add(new KeyValuePair<string, IShellCommand>(qualified, command));
                }
            }
        }

        return resolved;
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

            foreach (var (qualifiedName, command) in QualifiedCommands)
            {
                if (seen.Add(qualifiedName))
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

        if (_registry.TryGet(name, out command))
        {
            return true;
        }

        // A qualified name resolves through the module tree, by the walk the engine uses to
        // dispatch the call itself.
        foreach (var (qualifiedName, moduleCommand) in QualifiedCommands)
        {
            if (string.Equals(qualifiedName, name, StringComparison.OrdinalIgnoreCase))
            {
                command = moduleCommand;
                return true;
            }
        }

        return TryResolveUniqueModuleMember(name, out command);
    }

    /// <summary>
    /// Resolves a bare member name when exactly one module exports it.
    /// </summary>
    /// <remarks>
    /// Two modules exporting the same name is a genuine ambiguity, and choosing one silently
    /// would be worse than the "not found" this replaces — the caller can always qualify.
    /// </remarks>
    private bool TryResolveUniqueModuleMember(string name, out IShellCommand command)
    {
        IShellCommand? onlyMatch = null;

        foreach (var (qualifiedName, moduleCommand) in QualifiedCommands)
        {
            var separator = qualifiedName.LastIndexOf('.');
            var member = separator >= 0 ? qualifiedName[(separator + 1)..] : qualifiedName;

            if (!string.Equals(member, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (onlyMatch is not null)
            {
                command = null!;
                return false;
            }

            onlyMatch = moduleCommand;
        }

        command = onlyMatch!;
        return onlyMatch is not null;
    }

    /// <summary>
    /// Aliases come from the registry alone: a scope's command table stores each name as its own
    /// entry, so a scoped alias is already a command in <see cref="All"/> rather than an alias of
    /// one.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetAliasMap() => _registry.GetAliasMap();
}

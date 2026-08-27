namespace Tosh.Runtime;

/// <summary>
/// The command-table operations the language needs: look a name up, enumerate what is
/// known, and add or remove a name that a declaration created.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0006`, stage 2a. `ShellCommandRegistry` has eleven public members; the
/// language uses six. Naming those six is what lets a `ToastRuntime` hold a command
/// table without holding the shell's registry, and it keeps `RegisterAlias`,
/// `GetAliases` and `Get` on the registry alone — declaring an alias and
/// throw-on-missing lookup are shell concerns.
///
/// `GetAliasMap` is on the interface, though an earlier draft of this comment claimed
/// alias handling was shell-only. It is not: `ScopedCommandView` reports what a scope
/// can see, and since a scope's own table stores each name as a separate entry, aliases
/// can only come from the registry. Reading the alias map is language work; *declaring*
/// an alias is not.
/// </para>
/// <para>
/// **This is not a read-only view, and an earlier plan for one was wrong.** The intent
/// was to deny the language the ability to mutate the table, on the belief that it only
/// resolved names. It does not: a `global` or `export` function declaration must put a
/// name in the runtime table, or `export func greet() => "hi"` would not make `greet`
/// callable. Registration is the feature, not an oversight, so the interface includes
/// it and this comment records why rather than leaving the next reader to rediscover it.
/// </para>
/// <para>
/// <see cref="RegisterOrReplace"/> rather than <c>Register</c> is deliberate and is what
/// the language actually calls: redeclaring a function replaces it, and overloads are
/// merged into a single command before being written back.
/// </para>
/// </remarks>
public interface ICommandTable : IScopedCommandView
{
    /// <summary>Every registered command, ordered by name.</summary>
    new IEnumerable<IShellCommand> All { get; }

    /// <summary>Every registered name, including aliases.</summary>
    IEnumerable<string> AllNames { get; }

    /// <summary>Looks a name up, returning false rather than throwing when it is unknown.</summary>
    new bool TryGet(string name, out IShellCommand command);

    /// <summary>Adds a command, replacing any existing one of the same name.</summary>
    void RegisterOrReplace(IShellCommand command);

    /// <summary>Removes a name, returning whether anything was removed. Backs <c>forget</c>.</summary>
    bool Remove(string name);

    /// <summary>
    /// Canonical name to its aliases. Needed by <c>ScopedCommandView</c>, which reports
    /// what a scope can see: a scope's own table stores each name as its own entry, so
    /// aliases can only come from the registry.
    /// </summary>
    new IReadOnlyDictionary<string, IReadOnlyList<string>> GetAliasMap();
}

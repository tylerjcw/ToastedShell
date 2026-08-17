namespace Tosh.Runtime;

/// <summary>
/// The state a Tōast program needs, independent of any shell.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0006`, stage 2d. `ToshRuntime` carries 38 public members and the language
/// touches 23 of them; the other fifteen are display, terminal, session and
/// `$tosh.Last` state that Tōast never reads. This type holds the language's half, and
/// `ToshRuntime` composes one rather than inheriting from it.
/// </para>
/// <para>
/// **Composition rather than inheritance was chosen deliberately.** With a base class,
/// every member added later needs a judgement about which class it lands in — and that
/// judgement is exactly what produced `Config.Shell.MaxRecursionDepth`, a limit on the
/// evaluator filed under "Shell". Composition makes the question "does the language need
/// this?" rather than "which half does this feel like?", and only the first has an
/// answer that can be checked.
/// </para>
/// <para>
/// The test it exists to pass: a host constructs a `ToastRuntime` and nothing else, and
/// Tōast runs. That is the same test `TOSH-0003` sets for packaging — Tōast installed,
/// TōSh absent, a script still runs — and the reason `SELF_HOSTING_RFC.md` can describe
/// TōSh as a port target rather than a prerequisite.
/// </para>
/// <para>
/// This first slice carries the members whose side is not in question. `Commands`,
/// `Config`, `Output`, `Error`, `CurrentDirectory`, `Events` and `Formatter` are still
/// reached through `ToshRuntime`; each has an open question recorded on the item and
/// moves when its answer does.
/// </para>
/// </remarks>
public sealed class ToastRuntime
{
    /// <summary>Invokes members on values. See `TOAST-0006` on why this is not yet an interface.</summary>
    /// <remarks>
    /// The one member whose concrete type forecloses a `no_clr` target:
    /// <see cref="IObjectAccessor"/> and <see cref="ITypeResolver"/> are interfaces, so a
    /// native implementation can be substituted, while this is a sealed class. Making it
    /// an interface is recorded on the item as part of this stage.
    /// </remarks>
    public ReflectionInvoker Invoker { get; } = new();

    /// <summary>Reads members off values — an interface, so a native target can replace it.</summary>
    public IObjectAccessor ObjectAccessor { get; } = new ReflectionObjectAccessor();

    /// <summary>Resolves type names — an interface, for the same reason.</summary>
    public ITypeResolver TypeResolver { get; } = new DotNetTypeResolver();

    /// <summary>Settings the language owns, independent of any shell config file.</summary>
    public ToastOptions Options { get; } = new();

    /// <summary>Global variables.</summary>
    public IDictionary<string, object?> Variables { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Declared classes, records, traits and interfaces, by name.</summary>
    public IDictionary<string, object?> Classes { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Declared modules, by name.</summary>
    public IDictionary<string, object?> Modules { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Modules already loaded, so `require` is idempotent per session.</summary>
    public ISet<string> LoadedModules { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CLR types emitted for globally-declared <c>raw struct</c>s, keyed by declared name,
    /// so a global raw struct is nameable in a native signature.
    /// </summary>
    public IDictionary<string, Type> NativeTypes { get; } =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
}

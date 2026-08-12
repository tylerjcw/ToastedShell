using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// Resolves globally-declared <c>raw struct</c> names — bare or module-qualified
/// — to their emitted CLR types, falling through to the CLR resolver otherwise.
///
/// Layered under <see cref="ScopedTypeResolver"/> so lexically-scoped raw
/// structs shadow global ones, matching how every other declaration behaves.
/// </summary>
internal sealed class NativeTypeRegistryResolver : ITypeResolver
{
    private readonly ITypeResolver _baseResolver;
    private readonly IDictionary<string, Type> _nativeTypes;
    private readonly IDictionary<string, object?> _modules;

    /// <summary>Forwards to the base resolver, which owns the alias table — <c>TS-P2-37</c>.</summary>
    public Type? ResolveAliasCaseVariant(string name) => _baseResolver.ResolveAliasCaseVariant(name);

    public NativeTypeRegistryResolver(
        ITypeResolver baseResolver,
        IDictionary<string, Type> nativeTypes,
        IDictionary<string, object?> modules)
    {
        _baseResolver = baseResolver;
        _nativeTypes = nativeTypes;
        _modules = modules;
    }

    public Type? Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        if (_nativeTypes.TryGetValue(trimmed, out var nativeType))
        {
            return nativeType;
        }

        if (trimmed.Contains('.') &&
            NativeTypeQualifiedLookup.TryResolve(_modules, trimmed, out var qualified))
        {
            return qualified;
        }

        return _baseResolver.Resolve(name);
    }
}

/// <summary>
/// Walks a dotted path through nested module objects to a <c>raw struct</c> in
/// the innermost module's export table, so <c>ToastLib.System.SysInfo</c> is
/// nameable in a native signature declared outside that module.
///
/// Shared by both resolvers because modules can live in a lexical scope or at
/// the runtime root, and a qualified name must work either way.
/// </summary>
internal static class NativeTypeQualifiedLookup
{
    public static bool TryResolve(IDictionary<string, object?> modules, string name, out Type? type)
    {
        type = null;

        var segments = name.Split('.');
        if (segments.Length < 2) return false;

        if (!modules.TryGetValue(segments[0], out var root) || root is not ToshModuleObject module)
        {
            return false;
        }

        // Every segment but the last must name a module.
        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (!module.TryGetExportedModule(segments[index], out var nested))
            {
                return false;
            }

            module = nested;
        }

        return module.TryGetExportedNativeType(segments[^1], out type);
    }
}

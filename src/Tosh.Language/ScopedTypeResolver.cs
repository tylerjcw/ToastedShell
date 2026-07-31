using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ScopedTypeResolver : ITypeResolver
{
    private readonly ITypeResolver _baseResolver;
    private readonly IReadOnlyList<LexicalScope> _scopes;

    public ScopedTypeResolver(ITypeResolver baseResolver, IReadOnlyList<LexicalScope> scopes)
    {
        _baseResolver = baseResolver;
        _scopes = scopes;
    }

    public Type? Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        foreach (var scope in _scopes)
        {
            if (TryResolveInScope(scope, name, out var type))
            {
                return type;
            }
        }

        return _baseResolver.Resolve(name);
    }

    private Type? ResolveDirect(string name)
    {
        return _baseResolver.Resolve(name);
    }

    private bool TryResolveInScope(LexicalScope scope, string name, out Type? type)
    {
        // `raw struct` types are checked first. They are emitted at declaration
        // time and have no path for the base resolver to look up, so nothing
        // downstream could find them; and a native type name shadowing an alias
        // in the same scope is a collision `DeclareType` already rejects.
        if (scope.NativeTypes.TryGetValue(name, out var nativeType))
        {
            type = nativeType;
            return true;
        }

        // Qualified access to a raw struct exported from a module —
        // `Demo.Pair`, `ToastLib.System.SysInfo`. Modules nest, so this walks
        // the chain rather than assuming a single segment.
        if (name.Contains('.') && NativeTypeQualifiedLookup.TryResolve(scope.Modules, name, out type))
        {
            return true;
        }

        foreach (var (alias, targetPath) in scope.TypeAliases)
        {
            if (string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))
            {
                type = ResolveDirect(targetPath);
                return type is not null;
            }

            if (name.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase))
            {
                type = ResolveDirect(targetPath + name[alias.Length..]);
                return type is not null;
            }
        }

        foreach (var importPath in scope.TypeImports)
        {
            if (string.Equals(GetLastSegment(importPath), name, StringComparison.OrdinalIgnoreCase))
            {
                type = ResolveDirect(importPath);
                if (type is not null)
                {
                    return true;
                }
            }

            type = ResolveDirect(importPath + "." + name);
            if (type is not null)
            {
                return true;
            }
        }

        type = null;
        return false;
    }

    private static string GetLastSegment(string path)
    {
        var separatorIndex = path.LastIndexOf('.');
        return separatorIndex >= 0 ? path[(separatorIndex + 1)..] : path;
    }
}

using Tosh.Compiler.IR;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Binding;

/// <summary>
/// Compiler-only, syntax-level metadata for statically required scripts. This never executes
/// a library, loads an assembly, or builds a project. Requires remain runtime operations in
/// the bound tree; knowing an exported name is not a claim that its implementation is IL.
/// </summary>
internal sealed class RequiredTypeRegistry
{
    private readonly Dictionary<string, Scope> _files = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<ModuleDefinitionStatementSyntax, Scope> _modules = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoundType, Scope> _declarations = new(ReferenceEqualityComparer.Instance);
    private readonly Scope? _ambient;
    private readonly Scope _root;

    public RequiredTypeRegistry(ParseResult source, StatementSyntax? ambientTypes = null)
    {
        if (ambientTypes is not null)
        {
            _ambient = new Scope(parent: null, exportByDefault: false);
            foreach (var (name, type) in Lowerer.BuildUserTypeRegistry(ambientTypes))
                _ambient.Types[name] = new(type, false);
        }
        var directory = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(source.SourceName) && !source.SourceName.StartsWith('<') &&
            !source.SourceName.StartsWith("repl_entry", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(PathUtilities.ResolvePath(directory, source.SourceName)) ?? directory;
        }
        _root = new Scope(_ambient, exportByDefault: false);
        Visit(source.Statement, _root, directory);
    }

    public IReadOnlyDictionary<string, BoundType> Types => _root.VisibleTypes();

    public TypeNameResolver? ResolverFor(ModuleDefinitionStatementSyntax module) =>
        _modules.TryGetValue(module, out var scope) ? scope.Resolver : null;

    public BoundType ResolveMember(BoundType owner, string name, TypeNameResolver caller)
    {
        if (!_declarations.TryGetValue(owner, out var scope)) return caller.Resolve(name);

        var resolved = scope.AnnotationResolver.Resolve(name);
        // Some public contracts deliberately name the entry module into which their file
        // is imported (e.g. Api.Axes). Only qualified names may use that public caller view:
        // an unresolved bare name must never acquire an unrelated caller's local type.
        return resolved.IsDynamic && name.Contains('.') ? caller.Resolve(name) : resolved;
    }

    private void Visit(StatementSyntax statement, Scope scope, string directory)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) Visit(child, scope, directory);
                return;
            case ModuleDefinitionStatementSyntax module:
                // Partial bodies share exports, not lexical/private state. A new body may
                // see earlier public contributions, while declarations from the first body
                // retain their own private imports. Module aliases keep the shared view so
                // a later public contribution remains visible through every existing alias.
                var modulePath = scope.ModulePath is { Length: > 0 } prefix
                    ? prefix + "." + module.Name
                    : module.Name;
                var exports = (module.IsPartial ? scope.FindModule(module.Name) : null)
                    ?? new Scope(parent: null, exportByDefault: false, modulePath: modulePath);
                var childScope = new Scope(scope, exportByDefault: true, sharedExports: exports, modulePath: modulePath);
                childScope.SeedExports();
                scope.Modules[module.Name] = new(exports,
                    module.Modifier is DeclarationModifier.Default or DeclarationModifier.Export);
                _modules[module] = childScope;
                foreach (var child in module.Body.Statements) Visit(child, childScope, directory);
                childScope.PublishExports();
                return;
            case RequireStatementSyntax require when !require.IsNative:
                Import(require, scope, directory);
                return;
        }

        var declaration = statement switch
        {
            ClassDefinitionStatementSyntax c => (c.Name, c.Modifier, (BoundType)new UserClassType(c.Name, c, null)),
            RecordDefinitionStatementSyntax r => (r.Name, r.Modifier, (BoundType)new UserRecordType(r.Name, r, null)),
            StructDefinitionStatementSyntax s => (s.Name, s.Modifier, (BoundType)new UserStructType(s.Name, s, null)),
            UnionDefinitionStatementSyntax u => (u.Name, u.Modifier, (BoundType)new UserUnionType(u.Name, u, null)),
            EnumDefinitionStatementSyntax e => (e.Name, e.Modifier, (BoundType)new UserEnumType(e.Name, e, null)),
            InterfaceDefinitionStatementSyntax i => (i.Name, i.Modifier, (BoundType)new UserInterfaceType(i.Name, i, null)),
            TraitDefinitionStatementSyntax t => (t.Name, t.Modifier, (BoundType)new UserTraitType(t.Name, t, null)),
            TypeAliasStatementSyntax a => (a.Name, a.Modifier,
                (BoundType)new RefinementType(new TypeNameResolver(userTypes: scope.VisibleTypes()).Resolve(a.BaseTypeName), a.Name, a)),
            _ => (null, DeclarationModifier.Default, (BoundType?)null),
        };
        if (declaration.Item1 is { } name && declaration.Item3 is { } type)
        {
            scope.Types[name] = new(type, scope.Exports(declaration.Item2));
            _declarations[type] = scope;
        }
    }

    private void Import(RequireStatementSyntax require, Scope scope, string directory)
    {
        var library = ReadLibrary(require.Target, directory);
        if (library is null) return;
        var exported = scope.Exports(require.Modifier);
        if (require.Imports.Count == 0)
        {
            foreach (var (name, entry) in library.Types)
                if (entry.Exported) scope.Types[name] = new(entry.Value, exported);
            foreach (var (name, entry) in library.Modules)
                if (entry.Exported) scope.Modules[name] = new(entry.Value, exported);
            return;
        }

        foreach (var import in require.Imports)
        {
            var segments = import.Name.Split('.');
            var container = library;
            foreach (var segment in segments[..^1])
            {
                if (!container.Modules.TryGetValue(segment, out var nested) || !nested.Exported)
                {
                    container = null;
                    break;
                }
                container = nested.Value;
            }
            if (container is null) continue;
            var leaf = segments[^1];
            var binding = import.Alias ?? leaf;
            if (container.Modules.TryGetValue(leaf, out var module) && module.Exported)
                scope.Modules[binding] = new(module.Value, exported);
            else if (container.Types.TryGetValue(leaf, out var type) && type.Exported)
                scope.Types[binding] = new(type.Value, exported);
        }
    }

    private Scope? ReadLibrary(string target, string directory)
    {
        // As at runtime, an extensionless target may select a .tosh file. Other target
        // kinds are intentionally opaque here, particularly .csproj and native imports.
        try
        {
            var path = PathUtilities.ResolvePath(directory, target);
            if (!Path.HasExtension(path) && File.Exists(path + ".tosh")) path += ".tosh";
            if (!string.Equals(Path.GetExtension(path), ".tosh", StringComparison.OrdinalIgnoreCase)) return null;
            if (_files.TryGetValue(path, out var cached)) return cached;
            // The runtime diagnoses cyclic imports. Do not recursively discover forever,
            // including cycles spelled through successively different symlink paths.
            if (_active.Count >= 64 || !_active.Add(path)) return null;
            try
            {
                var parsed = ToshParser.Parse(File.ReadAllText(path), path);
                if (parsed.Diagnostics.Count != 0) return null;
                var scope = new Scope(_ambient, exportByDefault: false);
                Visit(parsed.Statement, scope, Path.GetDirectoryName(path)!);
                _files[path] = scope;
                return scope;
            }
            finally { _active.Remove(path); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unavailable metadata is unknown, not a concrete placeholder. Runtime require
            // keeps its existing missing-file diagnostic; strict annotations remain strict.
            return null;
        }
    }

    private sealed record Entry<T>(T Value, bool Exported);

    private sealed class Scope(Scope? parent, bool exportByDefault, Scope? sharedExports = null, string? modulePath = null)
    {
        public Dictionary<string, Entry<BoundType>> Types { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Entry<Scope>> Modules { get; } = new(StringComparer.Ordinal);
        public string? ModulePath { get; } = modulePath;
        private TypeNameResolver? _resolver;
        private TypeNameResolver? _annotationResolver;
        // Resolution starts only after the complete discovery walk, so siblings and later
        // reexports are present. No mutable process-global cache survives a lowering pass.
        public TypeNameResolver Resolver => _resolver ??= new TypeNameResolver(userTypes: VisibleTypes());
        public TypeNameResolver AnnotationResolver => _annotationResolver ??= new TypeNameResolver(userTypes: AnnotationTypes());

        public bool Exports(DeclarationModifier modifier) => modifier == DeclarationModifier.Export ||
            (modifier == DeclarationModifier.Default && exportByDefault);

        public Scope? FindModule(string name)
        {
            if (Modules.TryGetValue(name, out var local)) return local.Value;
            if (sharedExports?.Modules.TryGetValue(name, out var shared) == true) return shared.Value;
            return parent?.FindModule(name);
        }

        public void SeedExports()
        {
            if (sharedExports is null) return;
            // Like LexicalScope, a new partial body snapshots prior public bindings.
            // They remain lexical even if a later body replaces the shared export. They
            // must not be republished at the end and overwrite a newer contribution.
            foreach (var (name, entry) in sharedExports.Types)
                if (entry.Exported) Types[name] = new(entry.Value, false);
            foreach (var (name, entry) in sharedExports.Modules)
                if (entry.Exported) Modules[name] = new(entry.Value, false);
        }

        public void PublishExports()
        {
            if (sharedExports is null) return;
            foreach (var (name, entry) in Types)
                if (entry.Exported) sharedExports.Types[name] = entry;
            foreach (var (name, entry) in Modules)
                if (entry.Exported) sharedExports.Modules[name] = entry;
        }

        public Dictionary<string, BoundType> VisibleTypes()
        {
            var result = parent?.VisibleTypes() ?? new Dictionary<string, BoundType>(StringComparer.Ordinal);
            // The runtime consults this body's own bindings before its shared export table.
            // Keep that precedence when a later body exports the name of an earlier private
            // type. Public additions still reach old declarations via the shared view.
            sharedExports?.Flatten(result, prefix: "", onlyExports: true, new HashSet<Scope>(ReferenceEqualityComparer.Instance));
            Flatten(result, prefix: "", onlyExports: false, new HashSet<Scope>(ReferenceEqualityComparer.Instance));
            return result;
        }

        private Dictionary<string, BoundType> AnnotationTypes()
        {
            var result = VisibleTypes();
            var exports = sharedExports ?? this;
            // Member conversions consult DeclaringExports before captured lexical names.
            // The original module path is retained through selective aliases and partial
            // contributions, matching the runtime's self-qualified annotation lookup.
            foreach (var (name, entry) in exports.Types)
                if (entry.Exported) result[name] = entry.Value;
            if (exports.ModulePath is { Length: > 0 } path)
                exports.Flatten(result, path + ".", onlyExports: true, new HashSet<Scope>(ReferenceEqualityComparer.Instance));
            return result;
        }

        private void Flatten(Dictionary<string, BoundType> result, string prefix, bool onlyExports, HashSet<Scope> active)
        {
            if (!active.Add(this)) return;
            foreach (var (name, entry) in Types)
                if (!onlyExports || entry.Exported) result[prefix + name] = entry.Value;
            foreach (var (name, entry) in Modules)
            {
                if (onlyExports && !entry.Exported) continue;
                var modulePrefix = prefix + name + ".";
                // A nearer module shadows the whole outer module, not just the member
                // names it happens to redeclare. Never retain an inaccessible outer leaf.
                foreach (var key in result.Keys.Where(key => key.StartsWith(modulePrefix, StringComparison.Ordinal)).ToArray())
                    result.Remove(key);
                entry.Value.Flatten(result, modulePrefix, true, active);
            }
            active.Remove(this);
        }
    }
}

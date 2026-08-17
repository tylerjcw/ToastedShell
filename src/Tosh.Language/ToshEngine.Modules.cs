using System.Runtime.Loader;
using Tosh.Runtime;
using Tosh.Language.Bridge;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Modules and `require`: declaring a module, resolving a qualified name through one,
/// and loading an artifact — a `.tosh` script, an assembly or a project — into the
/// session once.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**; this
/// file is a relocation, not a rewrite.
///
/// Two members that read as belonging here do not, and were deliberately left behind:
/// `RequireMemberPath` validates a member path on a CLR type, and
/// `RequireMutableVariableBinding` resolves a variable for assignment. Both are named
/// for the *precondition* they enforce rather than for the `require` statement, which
/// is exactly the kind of resemblance a name-based split gets wrong.
/// </summary>
public sealed partial class ToshEngine
{

    /// <summary>
    /// Loads and imports a required module from a compiled assembly context, bypassing
    /// Tier-3 source replay. Called by compiled assemblies at runtime to satisfy
    /// <c>require</c> statements targeting external .tosh scripts or assemblies.
    /// </summary>
    public void RequireModuleFromCompiled(
        string target,
        string[] importedNames,
        string[] importedAliases,
        string resolveFrom)
    {
        var requirement = ResolveRequirement(target, resolveFrom);

        switch (requirement.Kind)
        {
            case RequireTargetKind.Script:
                {
                    if (!_requiredScripts.TryGetValue(requirement.CacheKey, out var artifact))
                    {
                        if (!_currentlyRequiring.Add(requirement.CacheKey))
                        {
                            throw new InvalidOperationException(
                                $"Circular require detected: '{requirement.CacheKey}' is already being loaded.");
                        }

                        try
                        {
                            var moduleSource = File.ReadAllText(requirement.ResolvedPath);
                            artifact = ExecuteRequiredScriptAsync(moduleSource, requirement.ResolvedPath, CancellationToken.None)
                                .GetAwaiter().GetResult();
                            _requiredScripts[requirement.CacheKey] = artifact;
                        }
                        finally
                        {
                            _currentlyRequiring.Remove(requirement.CacheKey);
                        }
                    }

                    ImportRequiredArtifact(artifact, importedNames, importedAliases);
                    break;
                }

            case RequireTargetKind.Assembly:
                {
                    if (importedNames.Length > 0)
                    {
                        throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                    }

                    if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                    {
                        break;
                    }

                    AssemblyLoadContext.Default.LoadFromAssemblyPath(requirement.ResolvedPath);
                    break;
                }

            case RequireTargetKind.Project:
                {
                    if (importedNames.Length > 0)
                    {
                        throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                    }

                    if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                    {
                        break;
                    }

                    var assemblyPath = BuildProjectAndResolveAssemblyPathAsync(requirement.ResolvedPath, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                    break;
                }

            default:
                throw new InvalidOperationException($"Unsupported require target kind '{requirement.Kind}'.");
        }
    }

    private void ImportRequiredArtifact(
        ToshRequiredScriptArtifact artifact,
        string[] importedNames,
        string[] importedAliases)
    {
        if (importedNames.Length == 0)
        {
            foreach (var (name, value) in artifact.Exports.Variables)
                DeclareVariable(name, ToVariableBinding(value), DeclarationModifier.Default);

            foreach (var (_, command) in artifact.Exports.Commands)
                DeclareCommand(command, DeclarationModifier.Default);

            foreach (var (name, type) in artifact.Exports.Types)
                DeclareType(name, type, DeclarationModifier.Default, artifact.Path);

            foreach (var (_, refinementType) in artifact.Exports.RefinementTypes)
                DeclareRefinementType(refinementType, DeclarationModifier.Default, artifact.Path);

            foreach (var (name, module) in artifact.Exports.Modules)
            {
                if (module is not null)
                    DeclareModule(name, module, DeclarationModifier.Default);
            }

            return;
        }

        for (var i = 0; i < importedNames.Length; i++)
        {
            var name = importedNames[i];
            var bindingName = (i < importedAliases.Length && !string.IsNullOrEmpty(importedAliases[i]))
                ? importedAliases[i]
                : name;

            // A dotted import name walks into nested modules, so a library organised
            // as `module Outer { module Inner { ... } }` can be imported as
            // `require Outer.Inner from "..." as Alias`. Without this only the
            // outermost name resolved, and the nested form reported the whole dotted
            // string as a missing export — accurate but unhelpful, since `Outer` was
            // there and `Inner` was inside it.
            if (name.Contains('.', StringComparison.Ordinal) &&
                TryResolveNestedExport(artifact, name, bindingName, DeclarationModifier.Default))
            {
                continue;
            }

            if (artifact.Exports.Modules.TryGetValue(name, out var module))
            {
                if (module is null)
                    throw new InvalidOperationException($"Export '{name}' in '{artifact.Path}' was null.");
                DeclareModule(bindingName, module, DeclarationModifier.Default);
                continue;
            }

            if (artifact.Exports.Types.TryGetValue(name, out var type))
            {
                DeclareType(bindingName, type, DeclarationModifier.Default, artifact.Path);
                continue;
            }

            if (artifact.Exports.RefinementTypes.TryGetValue(name, out var refinementType))
            {
                DeclareRefinementType(refinementType with { Name = bindingName }, DeclarationModifier.Default, artifact.Path);
                continue;
            }

            if (artifact.Exports.Commands.TryGetValue(name, out var command))
            {
                DeclareCommand(
                    string.Equals(bindingName, command.Name, StringComparison.Ordinal)
                        ? command
                        : new RenamedCommand(bindingName, command),
                    DeclarationModifier.Default);
                continue;
            }

            if (artifact.Exports.Variables.TryGetValue(name, out var value))
            {
                DeclareVariable(bindingName, ToVariableBinding(value), DeclarationModifier.Default);
                continue;
            }

            throw new InvalidOperationException($"Export '{name}' was not found in '{artifact.Path}'.");
        }
    }

    /// <summary>
    /// Resolves a dotted import name such as <c>Outer.Inner</c> by walking module
    /// exports, and declares whatever the final segment names.
    /// </summary>
    /// <remarks>
    /// Every segment but the last must be a module; the last may be anything a flat
    /// import can bring in. When the caller supplied no <c>as</c> alias the binding
    /// takes the *final* segment, because the dotted path is not itself a usable
    /// identifier.
    /// </remarks>
    private bool TryResolveNestedExport(
        ToshRequiredScriptArtifact artifact,
        string name,
        string bindingName,
        DeclarationModifier modifier)
    {
        var segments = name.Split('.', StringSplitOptions.None);

        if (segments.Length < 2 || segments.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        if (!artifact.Exports.Modules.TryGetValue(segments[0], out var root) ||
            root is not ToshModuleObject current)
        {
            return false;
        }

        // Walk to the module holding the final segment.
        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (!current.ExportTable.Modules.TryGetValue(segments[index], out var next) ||
                next is not ToshModuleObject nested)
            {
                return false;
            }

            current = nested;
        }

        var leaf = segments[^1];
        var binding = string.Equals(bindingName, name, StringComparison.Ordinal) ? leaf : bindingName;
        var exports = current.ExportTable;

        if (exports.Modules.TryGetValue(leaf, out var leafModule) && leafModule is not null)
        {
            DeclareModule(binding, leafModule, modifier);
            return true;
        }

        if (exports.Types.TryGetValue(leaf, out var leafType))
        {
            DeclareType(binding, leafType, modifier, artifact.Path);
            return true;
        }

        if (exports.RefinementTypes.TryGetValue(leaf, out var leafRefinement))
        {
            DeclareRefinementType(
                leafRefinement with { Name = binding },
                modifier,
                artifact.Path);
            return true;
        }

        if (exports.Commands.TryGetValue(leaf, out var leafCommand))
        {
            DeclareCommand(
                string.Equals(binding, leafCommand.Name, StringComparison.Ordinal)
                    ? leafCommand
                    : new RenamedCommand(binding, leafCommand),
                modifier);
            return true;
        }

        if (exports.Variables.TryGetValue(leaf, out var leafValue))
        {
            DeclareVariable(binding, ToVariableBinding(leafValue), modifier);
            return true;
        }

        return false;
    }

    private async IAsyncEnumerable<object?> EvaluateRequireStatementAsync(
        string sourceName,
        string sourceText,
        RequireStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            if (statement.IsNative)
            {
                if (statement.Imports.Count > 0)
                {
                    throw new InvalidOperationException("Selective require imports are not supported for native libraries.");
                }

                var moduleName = statement.Alias ?? GetDefaultNativeModuleName(statement.Target);
                EnsureNativeModuleAvailable(sourceName, statement.Target, moduleName, statement.Modifier);
            }
            else
            {
                var requirement = ResolveRequirement(statement.Target, GetExecutionDirectory(sourceName));

                switch (requirement.Kind)
                {
                    case RequireTargetKind.Script:
                        {
                            if (!_requiredScripts.TryGetValue(requirement.CacheKey, out var artifact))
                            {
                                if (!_currentlyRequiring.Add(requirement.CacheKey))
                                {
                                    throw new InvalidOperationException(
                                        $"Circular require detected: '{requirement.CacheKey}' is already being loaded.");
                                }

                                try
                                {
                                    var moduleSource = await File.ReadAllTextAsync(requirement.ResolvedPath, cancellationToken);
                                    artifact = await ExecuteRequiredScriptAsync(moduleSource, requirement.ResolvedPath, cancellationToken);
                                    _requiredScripts[requirement.CacheKey] = artifact;
                                }
                                finally
                                {
                                    _currentlyRequiring.Remove(requirement.CacheKey);
                                }
                            }

                            ImportRequiredArtifact(sourceName, sourceText, artifact, statement);
                            break;
                        }

                    case RequireTargetKind.Assembly:
                        {
                            if (statement.Imports.Count > 0)
                            {
                                throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                            }

                            if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                            {
                                break;
                            }

                            AssemblyLoadContext.Default.LoadFromAssemblyPath(requirement.ResolvedPath);
                            break;
                        }

                    case RequireTargetKind.Project:
                        {
                            if (statement.Imports.Count > 0)
                            {
                                throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                            }

                            if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                            {
                                break;
                            }

                            var assemblyPath = await BuildProjectAndResolveAssemblyPathAsync(requirement.ResolvedPath, cancellationToken);
                            AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Unsupported require target kind '{requirement.Kind}'.");
                }
            }
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.require_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: $"while requiring '{statement.Target}'"));
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateModuleDefinitionAsync(
        string sourceName,
        string sourceText,
        ModuleDefinitionStatementSyntax module,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, module.Name, module.Span, "reserved runtime namespace");

        // Partial modules merge their members into an existing module of the
        // same name. We pre-seed the new module scope with the existing
        // exports so that name resolution inside the partial body sees prior
        // contributions, and we re-use the same ModuleExportTable so that all
        // ToshModuleObject views observe the merged state automatically.
        ToshModuleObject? existingModule = null;
        ModuleExportTable? sharedExports = null;
        if (module.IsPartial && TryFindExistingModule(module.Name, out existingModule))
        {
            // Classes, records and structs all refuse this; modules merged
            // silently, which is the one place the four kinds disagreed.
            if (!existingModule.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_mismatch",
                    Title: $"Cannot extend module '{module.Name}' as partial: the original module was not declared as partial.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: module.Span,
                    Label: "both declarations must be partial"));
            }

            sharedExports = existingModule.ExportTable;
        }

        var moduleScope = new LexicalScope(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            isModuleScope: true,
            exportDeclarationsByDefault: true,
            exports: sharedExports);

        if (sharedExports is not null)
        {
            // Make prior exports visible to body-local resolution. Variables /
            // types / commands / refinements / nested modules are all copied
            // by reference so updates from the new body still flow through to
            // the shared export table.
            foreach (var (key, value) in sharedExports.Variables) moduleScope.Variables[key] = value;
            foreach (var (key, value) in sharedExports.Commands) moduleScope.Commands[key] = value;
            foreach (var (key, value) in sharedExports.Types) moduleScope.Classes[key] = value;
            foreach (var (key, value) in sharedExports.RefinementTypes) moduleScope.RefinementTypes[key] = value;
            foreach (var (key, value) in sharedExports.Modules) moduleScope.Modules[key] = value;
        }

        using (PushScope(moduleScope))
        {
            await foreach (var _ in ExecuteBlockAsync(sourceName, sourceText, module.Body, cancellationToken, pushNewScope: false)
                               .WithCancellation(cancellationToken))
            {
            }
        }

        // A partial declaration that merged into an existing module still
        // *declares* that module here, rather than returning early. The shared
        // ModuleExportTable means this is the same object and not a copy, so
        // there is nothing to keep in step — but without the declaration, a file
        // contributing a partial exported nothing under the name, and
        // `require Sys from "./b.tosh"` failed with "Export 'Sys' was not found"
        // while the merge had in fact succeeded. The bare `require "./b.tosh"`
        // form worked only because it never looks a name up.
        var moduleObject = existingModule
            ?? new ToshModuleObject(this, module.Name, moduleScope.Exports ?? new ModuleExportTable());

        var effectiveModifier = module.Modifier;

        if (effectiveModifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek().IsModuleScope)
        {
            effectiveModifier = DeclarationModifier.Export;
        }

        moduleObject.IsPartial = module.IsPartial;
        DeclareModule(module.Name, moduleObject, effectiveModifier);
        yield break;
    }

    private bool TryFindExistingModule(string name, out ToshModuleObject module)
    {
        // Walk inner scopes outward (most-nested first), then runtime, looking
        // for a previously declared module with this name.
        foreach (var scope in _scopes)
        {
            if (scope.Modules.TryGetValue(name, out var scoped) && scoped is ToshModuleObject scopedModule)
            {
                module = scopedModule;
                return true;
            }

            if (scope.IsModuleScope &&
                scope.Exports is { } exports &&
                exports.Modules.TryGetValue(name, out var exported) &&
                exported is ToshModuleObject exportedModule)
            {
                module = exportedModule;
                return true;
            }
        }

        if (Runtime.Modules.TryGetValue(name, out var runtimeModule) && runtimeModule is ToshModuleObject runtime)
        {
            module = runtime;
            return true;
        }

        module = null!;
        return false;
    }

    /// <summary>
    /// Decides whether <paramref name="path"/> names a shell symbol, and which one.
    /// </summary>
    /// <remarks>
    /// The segment arithmetic here is what the two <c>TryInvokeShellSymbol</c> twins duplicated:
    /// that a module call needs at least two segments, that everything between the module and the
    /// final name is a member path, and that a shell static type is matched only at exactly two
    /// segments. Both twins then invoked the same way, differing only in awaiting
    /// (<c>TS-P1-24</c>).
    /// </remarks>
    /// <summary>
    /// Whether <paramref name="module"/> exports <paramref name="segment"/>, so a
    /// dotted path beginning with the module's name belongs to it.
    /// </summary>
    /// <remarks>
    /// `TS-P2-100`. The question is asked before the module claims the path,
    /// rather than answered by letting the walk fail: a failed walk reports
    /// "member not found on ToshModuleObject", which names the module rather than
    /// the namespace the reader meant, and leaves no chance to try the CLR.
    /// </remarks>
    private bool ModuleClaimsPath(ToshModuleObject module, string head, string segment)
    {
        if (module.TryGetMember(segment, out _, includeHidden: true))
        {
            return true;
        }

        // The module does not export it. Yield the path only when there is no
        // *type* of the module's name to fall through to — that case already
        // works, and is what `TS-P1-35` built: `module Math` calling `Math.Max`
        // reaches the shadowed type and answers with its overloads. Skipping the
        // module branch there would hand the name to a different `Math` and
        // silently change `Max(3, 7)` from `Int32` to `Double`, which is what the
        // first attempt at this did.
        //
        // A namespace has no such fall-through, and that is the whole of
        // `TS-P2-100`: nothing named `System` is a type, so the module swallowed
        // the namespace with no way back.
        return TryResolveShellStaticType(head, out _) || ResolveTypeName(head) is not null;
    }

    private void DeclareModule(string name, object module, DeclarationModifier modifier)
    {
        EnsureReservedBindingName(name);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.Modules[name] = module;
            moduleScope.Exports!.Modules[name] = module;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.Modules[name] = module;
            exportScope.Exports!.Modules[name] = module;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy module declarations require a function, block, or module scope.");
            }

            _scopes.Peek().Modules[name] = module;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            Runtime.Modules[name] = module;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Modules[name] = module;
            return;
        }

        Runtime.Modules[name] = module;
    }

    /// <summary>
    /// Looks up a user-defined named type (class, record, struct, enum,
    /// union, interface, trait) in the engine's scope or runtime registry.
    /// Exposed publicly so compiled tosh (the IL emitter's host bridge)
    /// can resolve types for <c>new</c>-expressions without re-parsing.
    /// </summary>
    /// <summary>
    /// Resolves a module-qualified type name — <c>Outer.Inner.SmallInt</c> — by walking module
    /// exports, so a type declared inside a module can be named from outside it.
    /// </summary>
    /// <remarks>
    /// Without this, a module-qualified name could not be used as an annotation at all:
    /// `var x: ToastLib.Math.IntPercent = 60` reported `annotation_unknown_type` even though
    /// the unqualified `IntPercent` worked, and the same was true of a `class` or `record`
    /// declared in a module. Every lookup on the annotation path — refinement types, named
    /// types, and the CLR resolver — took a flat name (<c>TS-P1-34</c>).
    ///
    /// Deliberately placed beneath the flat lookups in both callers, so an unqualified name
    /// keeps resolving exactly as it did and only a dotted name reaches the walk. Shares the
    /// shape of `TryResolveNestedExport`, which does the same walk for `require` — the third
    /// place this programme has needed "follow a dotted path through modules".
    /// </remarks>
    private bool TryResolveQualifiedModuleMember(string name, out object? member)
    {
        member = null;

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = name.Split('.', StringSplitOptions.None);

        if (segments.Length < 2 || segments.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        if (!TryFindExistingModule(segments[0], out var current))
        {
            return false;
        }

        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (!current.ExportTable.Modules.TryGetValue(segments[index], out var next) ||
                next is not ToshModuleObject nested)
            {
                return false;
            }

            current = nested;
        }

        var leaf = segments[^1];

        if (current.ExportTable.RefinementTypes.TryGetValue(leaf, out var refinement))
        {
            member = refinement;
            return true;
        }

        if (current.ExportTable.Types.TryGetValue(leaf, out var type))
        {
            member = type;
            return true;
        }

        return false;
    }

    private bool TryGetModule(string name, out ToshModuleObject module)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Modules.TryGetValue(name, out var scopedModule) &&
                scopedModule is ToshModuleObject scopedToshModule)
            {
                module = scopedToshModule;
                return true;
            }
        }

        if (Runtime.Modules.TryGetValue(name, out var rawModule) &&
            rawModule is ToshModuleObject runtimeModule)
        {
            module = runtimeModule;
            return true;
        }

        module = null!;
        return false;
    }

    private bool TryResolveModuleQualifiedCommand(string qualifiedName, out IShellCommand command)
    {
        var segments = qualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || !TryGetModule(segments[0], out var module))
        {
            command = null!;
            return false;
        }

        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (!module.ExportTable.Modules.TryGetValue(segments[index], out var nested) ||
                nested is not ToshModuleObject nestedModule)
            {
                command = null!;
                return false;
            }

            module = nestedModule;
        }

        if (module.ExportTable.Commands.TryGetValue(segments[^1], out var resolved))
        {
            command = resolved;
            return true;
        }

        command = null!;
        return false;
    }

    /// <summary>
    /// Every visible module, flattened, each under the name a caller writes.
    /// </summary>
    /// <remarks>
    /// `TS-P2-68`. Introspection needs the same reach the engine has. Recursion is depth-limited
    /// because a `partial module` may be extended anywhere, and a cycle through the export tables
    /// is cheaper to bound than to prove impossible.
    /// </remarks>
    internal IReadOnlyList<ShellModuleSummary> EnumerateVisibleModules()
    {
        var summaries = new List<ShellModuleSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in _scopes)
        {
            foreach (var (name, value) in scope.Modules)
            {
                Collect(name, value, depth: 0);
            }
        }

        foreach (var (name, value) in Runtime.Modules)
        {
            Collect(name, value, depth: 0);
        }

        return summaries;

        void Collect(string qualifiedName, object? value, int depth)
        {
            if (depth > 16 || value is not ToshModuleObject module || !seen.Add(qualifiedName))
            {
                return;
            }

            var nested = module.ExportTable.Modules
                .Select(entry => $"{qualifiedName}.{entry.Key}")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            summaries.Add(new ShellModuleSummary(
                qualifiedName,
                module.ExportTable.Commands.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                nested,
                module.ExportTable.Types.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                module.ExportTable.Variables.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()));

            foreach (var (childName, childValue) in module.ExportTable.Modules)
            {
                Collect($"{qualifiedName}.{childName}", childValue, depth + 1);
            }
        }
    }

    /// <summary>
    /// Finds a module's exported command by its qualified name, walking the same module tree
    /// <see cref="TryResolveModuleQualifiedCommand"/> walks to dispatch the call.
    /// </summary>
    internal bool TryGetModuleCommandByQualifiedName(string qualifiedName, out IShellCommand command)
    {
        var separator = qualifiedName.LastIndexOf('.');

        if (separator > 0)
        {
            var moduleName = qualifiedName[..separator];
            var memberName = qualifiedName[(separator + 1)..];

            foreach (var scope in _scopes)
            {
                if (TryFromModuleTree(scope.Modules, moduleName, memberName, out command))
                {
                    return true;
                }
            }

            if (TryFromModuleTree(Runtime.Modules, moduleName, memberName, out command))
            {
                return true;
            }
        }

        command = null!;
        return false;

        static bool TryFromModuleTree(
            IEnumerable<KeyValuePair<string, object?>> roots,
            string moduleName,
            string memberName,
            out IShellCommand found)
        {
            var segments = moduleName.Split('.', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length > 0)
            {
                var rootValue = roots.FirstOrDefault(entry => entry.Key == segments[0]).Value;

                if (rootValue is ToshModuleObject rootModule)
                {
                    var current = rootModule;

                    for (var index = 1; index < segments.Length; index++)
                    {
                        if (!current.ExportTable.Modules.TryGetValue(segments[index], out var next) ||
                            next is not ToshModuleObject nextModule)
                        {
                            found = null!;
                            return false;
                        }

                        current = nextModule;
                    }

                    if (current.ExportTable.Commands.TryGetValue(memberName, out var resolved))
                    {
                        found = resolved;
                        return true;
                    }
                }
            }

            found = null!;
            return false;
        }
    }

    private bool TryGetNearestModuleScope(out LexicalScope moduleScope)
    {
        foreach (var scope in _scopes)
        {
            if (scope.IsModuleScope)
            {
                moduleScope = scope;
                return true;
            }
        }

        moduleScope = null!;
        return false;
    }

    private void ImportRequiredArtifact(
        string sourceName,
        string sourceText,
        ToshRequiredScriptArtifact artifact,
        RequireStatementSyntax statement)
    {
        if (statement.Imports.Count == 0)
        {
            // `TS-P2-62`. `require` imports a file's exports, and a file with none imports
            // nothing — which used to happen in silence, so a missing `export` looked exactly
            // like a missing file until something later failed to resolve. `source` is the
            // spelling for running a file's every declaration in the current scope.
            if (artifact.Exports.Variables.Count == 0 &&
                artifact.Exports.Commands.Count == 0 &&
                artifact.Exports.Types.Count == 0 &&
                artifact.Exports.RefinementTypes.Count == 0 &&
                artifact.Exports.Modules.Count == 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.require_exports_nothing",
                    Title: $"'{statement.Target}' declares no exports, so this require imports nothing.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: "nothing in this file is marked 'export'",
                    Help: "mark a declaration with 'export' to make it importable, or use "
                        + $"'source \"{statement.Target}\"' to run the file in the current scope."));
            }

            foreach (var (name, value) in artifact.Exports.Variables)
            {
                DeclareVariable(name, ToVariableBinding(value), statement.Modifier);
            }

            foreach (var (name, command) in artifact.Exports.Commands)
            {
                DeclareCommand(command, statement.Modifier);
            }

            foreach (var (name, type) in artifact.Exports.Types)
            {
                DeclareType(name, type, statement.Modifier, sourceName, sourceText, statement.Span);
            }

            foreach (var (_, refinementType) in artifact.Exports.RefinementTypes)
            {
                DeclareRefinementType(refinementType, statement.Modifier, sourceName, sourceText, statement.Span);
            }

            foreach (var (name, module) in artifact.Exports.Modules)
            {
                if (module is not null)
                {
                    DeclareModule(name, module, statement.Modifier);
                }
            }

            return;
        }

        foreach (var import in statement.Imports)
        {
            var bindingName = import.Alias ?? import.Name;

            // Both overloads route through the same resolver. The first fix landed
            // only on the other one, which is dead for this path — `require X.Y from
            // …` comes through here, so the dotted form kept failing while the code
            // to support it sat twelve thousand lines away, compiled and unreachable.
            if (import.Name.Contains('.', StringComparison.Ordinal) &&
                TryResolveNestedExport(artifact, import.Name, bindingName, statement.Modifier))
            {
                continue;
            }

            if (artifact.Exports.Modules.TryGetValue(import.Name, out var module))
            {
                if (module is null)
                {
                    throw new InvalidOperationException($"Export '{import.Name}' in '{artifact.Path}' was null.");
                }

                DeclareModule(bindingName, module, statement.Modifier);
                continue;
            }

            if (artifact.Exports.Types.TryGetValue(import.Name, out var type))
            {
                DeclareType(bindingName, type, statement.Modifier, sourceName, sourceText, import.Span);
                continue;
            }

            if (artifact.Exports.RefinementTypes.TryGetValue(import.Name, out var refinementType))
            {
                DeclareRefinementType(refinementType with { Name = bindingName }, statement.Modifier, sourceName, sourceText, import.Span);
                continue;
            }

            if (artifact.Exports.Commands.TryGetValue(import.Name, out var command))
            {
                DeclareCommand(
                    string.Equals(bindingName, command.Name, StringComparison.Ordinal)
                        ? command
                        : new RenamedCommand(bindingName, command),
                    statement.Modifier);
                continue;
            }

            if (artifact.Exports.Variables.TryGetValue(import.Name, out var value))
            {
                DeclareVariable(bindingName, ToVariableBinding(value), statement.Modifier);
                continue;
            }

            throw new InvalidOperationException($"Export '{import.Name}' was not found in '{artifact.Path}'.");
        }
    }

    private async Task<ToshRequiredScriptArtifact> ExecuteRequiredScriptAsync(
        string source,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var parseResult = Parse(source, sourceName);

        if (parseResult.Diagnostics.Count > 0)
        {
            throw new ToshDiagnosticException(parseResult.Diagnostics
                .Select(diagnostic => new ToshDiagnostic(
                    Code: diagnostic.Code,
                    Title: diagnostic.Title,
                    SourceName: parseResult.SourceName,
                    SourceText: parseResult.SourceText,
                    Span: diagnostic.Span,
                    Label: diagnostic.Label,
                    Help: diagnostic.Help))
                .ToArray());
        }

        var moduleScope = new LexicalScope(new Dictionary<string, object?>(StringComparer.Ordinal), isModuleScope: true);
        _scriptNameStack.Push(parseResult.SourceName);
        using var _ = PushScope(moduleScope);

        try
        {
            await foreach (var __ in EvaluateStatementAsync(
                               parseResult.SourceName,
                               parseResult.SourceText,
                               parseResult.Statement,
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
            }
        }
        catch (ReturnSignalException signal)
        {
            UpdateLastResultIfAny(signal.Values);
        }
        catch (BreakSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "break",
                code: "tosh.runtime.break_outside_loop",
                title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        catch (ContinueSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "continue",
                code: "tosh.runtime.continue_outside_loop",
                title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        finally
        {
            _scriptNameStack.Pop();
        }

        return new ToshRequiredScriptArtifact(sourceName, moduleScope.Exports ?? new ModuleExportTable());
    }

    private static RequireTarget ResolveRequirement(string target, string currentDirectory)
    {
        var candidate = PathUtilities.ResolvePath(currentDirectory, target);

        if (!Path.HasExtension(candidate))
        {
            var toshCandidate = candidate + ".tosh";

            if (File.Exists(toshCandidate))
            {
                return new RequireTarget(RequireTargetKind.Script, toshCandidate, toshCandidate);
            }
        }

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Required target '{candidate}' was not found.", candidate);
        }

        return Path.GetExtension(candidate).ToLowerInvariant() switch
        {
            ".tosh" => new RequireTarget(RequireTargetKind.Script, candidate, candidate),
            ".dll" => new RequireTarget(RequireTargetKind.Assembly, candidate, candidate),
            ".csproj" => new RequireTarget(RequireTargetKind.Project, candidate, candidate),
            _ => throw new InvalidOperationException($"Unsupported require target '{candidate}'. ToSh currently supports .tosh, .dll, and .csproj targets."),
        };
    }

    private sealed record RequireTarget(RequireTargetKind Kind, string ResolvedPath, string CacheKey);
}

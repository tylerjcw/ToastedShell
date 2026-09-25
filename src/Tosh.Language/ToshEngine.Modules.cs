using System.Runtime.Loader;
using System.Text.RegularExpressions;

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
                        : RenamedCommand.Create(bindingName, command),
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
                    : RenamedCommand.Create(binding, leafCommand),
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
            else if (await TryImportFromModuleAsync(sourceName, sourceText, statement, cancellationToken))
            {
                // Named members taken from a module rather than a file.
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

                            if (!LanguageRuntime.LoadedModules.Add(requirement.CacheKey))
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

                            if (!LanguageRuntime.LoadedModules.Add(requirement.CacheKey))
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
            exports: sharedExports)
        {
            ModuleName = module.Name,
        };

        // `TOAST-0122`. The dotted path this module is reached by, built from the module
        // scopes already on the stack. A partial that merges into an existing module
        // shares its table and so already has the path, which is the same string.
        if (moduleScope.Exports is { QualifiedName: null } freshExports)
        {
            freshExports.QualifiedName = BuildModulePath(module.Name);
        }

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

    /// <summary>
    /// The dotted path a module being declared will be reached by — <c>ToastLib.Math</c>
    /// for a <c>Math</c> declared inside <c>ToastLib</c>. Built from the module scopes
    /// already on the stack, outermost first (`TOAST-0122`).
    /// </summary>
    private string BuildModulePath(string name)
    {
        List<string>? enclosing = null;

        // `_scopes` enumerates innermost-first, so the names come out reversed.
        foreach (var scope in _scopes)
        {
            if (scope.IsModuleScope && scope.ModuleName is { Length: > 0 } enclosingName)
            {
                (enclosing ??= new List<string>()).Add(enclosingName);
            }
        }

        if (enclosing is null)
        {
            return name;
        }

        enclosing.Reverse();
        enclosing.Add(name);
        return string.Join('.', enclosing);
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

        if (LanguageRuntime.Modules.TryGetValue(name, out var runtimeModule) && runtimeModule is ToshModuleObject runtime)
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
            LanguageRuntime.Modules[name] = module;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Modules[name] = module;
            return;
        }

        LanguageRuntime.Modules[name] = module;
    }

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

        if (LanguageRuntime.Modules.TryGetValue(name, out var rawModule) &&
            rawModule is ToshModuleObject runtimeModule)
        {
            module = runtimeModule;
            return true;
        }

        // `TOAST-0141`. A sibling, reached through the module this code belongs to.
        //
        // `module Build { module Publish { … } module Packaging { … } }` in one file resolves
        // `Packaging` from inside `Publish` because both are declared into the one scope.
        // Split across files as `partial module Build.Publish` and `partial module
        // Build.Packaging`, each `require` gets a scope of its own, and the loop above finds
        // only what that scope holds — so a module required *later* was invisible and the
        // call failed at runtime with "Unable to resolve .NET access path". Splitting a module
        // tree into a file per module changed what a name meant.
        //
        // The enclosing module's *export table* is shared across its partial declarations —
        // `TOAST-0122` reuses one `ModuleExportTable` so every view observes the merged state
        // — so the sibling is already there at call time, just not in the scope's own
        // dictionary. Looking at the exports is enough; no new bookkeeping is needed.
        //
        // Last, deliberately: everything above still wins, so this can only answer names that
        // failed before and cannot shadow a local, an import, or a nearer module.
        foreach (var scope in _scopes)
        {
            if (scope.Exports is { } exports &&
                exports.Modules.TryGetValue(name, out var sibling) &&
                sibling is ToshModuleObject siblingModule)
            {
                module = siblingModule;
                return true;
            }
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

        foreach (var (name, value) in LanguageRuntime.Modules)
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

            if (TryFromModuleTree(LanguageRuntime.Modules, moduleName, memberName, out command))
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

            // `require ToastLib.Gl as Gl`. The name goes on the module the file declares,
            // found the same way a dotted selective import finds it. A file that declares no
            // module still has exports, so the name goes on a module view of those instead —
            // otherwise naming a library would depend on whether its author wrote a `module`
            // line, and the failure would read as a missing export nobody had asked for.
            if (statement.Alias is { Length: > 0 } moduleAlias)
            {
                if (!TryResolveNestedExport(artifact, statement.Target, moduleAlias, statement.Modifier))
                {
                    DeclareModule(
                        moduleAlias,
                        artifact.Exports.Modules.TryGetValue(statement.Target, out var declared)
                            && declared is ToshModuleObject declaredModule
                                ? declaredModule
                                : new ToshModuleObject(this, moduleAlias, artifact.Exports),
                        statement.Modifier);
                }

                return;
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
                        : RenamedCommand.Create(bindingName, command),
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

    /// <summary>
    /// Where <c>require &lt;target&gt;</c> points.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name is looked for in the reader's library first, and only then beside the script
    /// that asked. That order is deliberate: a library name should mean the same thing
    /// wherever it is required from, rather than depending on which directory the shell
    /// happens to be running in. A stray <c>Shell.tosh</c> next to a script cannot quietly
    /// take the place of the library's.
    /// </para>
    /// <para>
    /// An extension is how you say you mean a file. <c>require foo.tosh</c> is the file
    /// <c>foo.tosh</c>, never the library name <c>foo/tosh</c> — and the same for
    /// <c>.dll</c> and <c>.csproj</c>, because an extension is the same signal there. So is
    /// anything with a separator, or a rooted, <c>~</c> or <c>./</c> prefix: those were
    /// clearly written as paths.
    /// </para>
    /// </remarks>
    private RequireTarget ResolveRequirement(string target, string currentDirectory)
    {
        if (TryResolveLibraryName(target) is { } fromLibrary)
        {
            return fromLibrary;
        }

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
            // Naming both places, because a name that was meant for the library resolves to
            // a path beside the script and the message would otherwise point somewhere
            // nobody wrote.
            var library = IsLibraryName(target) && LanguageRuntime.LibraryDirectoryProvider?.Invoke() is { Length: > 0 } root
                ? $" It is not in the library at '{Path.GetFullPath(root)}' either, as '{target.Replace('.', Path.DirectorySeparatorChar)}.tosh' or 'init.tosh' beside it."
                : string.Empty;

            throw new FileNotFoundException($"Required target '{candidate}' was not found.{library}", candidate);
        }

        return Path.GetExtension(candidate).ToLowerInvariant() switch
        {
            ".tosh" => new RequireTarget(RequireTargetKind.Script, candidate, candidate),
            ".dll" => new RequireTarget(RequireTargetKind.Assembly, candidate, candidate),
            ".csproj" => new RequireTarget(RequireTargetKind.Project, candidate, candidate),
            _ => throw new InvalidOperationException($"Unsupported require target '{candidate}'. ToSh currently supports .tosh, .dll, and .csproj targets."),
        };
    }

    /// <summary>
    /// Imports named members from a module, rather than from a file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>require { Clamp, IntegerPart as iPart } from ToastLib.Math</c>. A module is the
    /// unit of meaning here and a file is where some of it happens to live: a partial module
    /// is spread over as many files as it likes, so two members of one module can come from
    /// two different files, and each is loaded only if it is asked for.
    /// </para>
    /// <para>
    /// Tried before the path, but only when the module holds <em>every</em> name the
    /// statement asks for. A name that is both — `ToastLib.Math` is a module here and also
    /// the file that aggregates it — resolves to the module, which loads the one file
    /// holding the member instead of the dozen the aggregator pulls in. Where the module
    /// cannot supply them all, nothing is loaded and the path takes over unchanged, so a
    /// require that worked before still means what it did.
    /// </para>
    /// </remarks>
    private async ValueTask<bool> TryImportFromModuleAsync(
        string sourceName,
        string sourceText,
        RequireStatementSyntax statement,
        CancellationToken cancellationToken)
    {
        if (statement.Imports.Count == 0 || !TryBuildLibraryExportIndex(out var index))
        {
            return false;
        }

        // Every requested member must be in the module before anything is loaded.
        var wanted = new List<(RequireImportSyntax Import, string File)>();

        foreach (var import in statement.Imports)
        {
            if (!index.TryGetValue((statement.Target, import.Name), out var files))
            {
                return false;
            }

            if (files.Count > 1)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.require.ambiguous_export",
                    Title: $"'{statement.Target}.{import.Name}' is exported by more than one file.",
                    Help: $"{string.Join(" and ", files)} both export it. Require the one you mean."));
            }

            wanted.Add((import, files[0]));
        }

        foreach (var (import, file) in wanted)
        {
            var artifact = await LoadRequiredScriptAsync(file, cancellationToken);

            // Asked for by its full path inside the file, because the member lives in the
            // module rather than at the file's top level — the dotted walk that already
            // serves `require Outer.Inner from …`. It binds under the name written, or the
            // alias if one was given.
            ImportRequiredArtifact(
                artifact,
                [$"{statement.Target}.{import.Name}"],
                [import.Alias ?? import.Name]);
        }

        return true;
    }

    /// <summary>Loads a script once, with the caching and cycle detection a require has.</summary>
    private async ValueTask<ToshRequiredScriptArtifact> LoadRequiredScriptAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (_requiredScripts.TryGetValue(path, out var cached))
        {
            return cached;
        }

        if (!_currentlyRequiring.Add(path))
        {
            throw new InvalidOperationException(
                $"Circular require detected: '{path}' is already being loaded.");
        }

        try
        {
            var source = await File.ReadAllTextAsync(path, cancellationToken);
            var artifact = await ExecuteRequiredScriptAsync(source, path, cancellationToken);

            _requiredScripts[path] = artifact;
            return artifact;
        }
        finally
        {
            _currentlyRequiring.Remove(path);
        }
    }

    /// <summary>
    /// Where each exported name lives, keyed by the module that owns it.
    /// </summary>
    /// <remarks>
    /// Built once, on the first qualified name that fails to resolve, and never for a
    /// session that has no such name. Measured on an 89-file library: 306 entries in about
    /// 7 ms — cheaper than the require it saves, so there is nothing to cache and nothing to
    /// invalidate.
    /// </remarks>
    private Dictionary<(string Module, string Member), List<string>>? _libraryExports;

    private static readonly Regex ModuleDeclaration = new(
        @"^export\s+(?:partial\s+)?module\s+([A-Za-z][A-Za-z0-9_.]*)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ExportedMember = new(
        @"^export\s+(?:partial\s+|sealed\s+|static\s+|shared\s+|fixed\s+)*(?:func|class|record|enum|interface|union|trait|var|const|type)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Loads the library module that would supply a qualified name, if exactly one would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ToastLib.Math.Clamp(…)</c> asks for <c>Clamp</c> in module <c>ToastLib.Math</c>.
    /// A module name is not a file path — a partial module is spread across as many files as
    /// it likes — so the answer comes from an index of what each file exports rather than
    /// from probing directories.
    /// </para>
    /// <para>
    /// Qualifying by module is what makes this safe. Thirteen names in that library are
    /// exported by more than one file; none of them collide once the module is named, so
    /// nothing has to be guessed. Where two files in one module do export the same name, it
    /// is reported rather than picked.
    /// </para>
    /// </remarks>
    private async ValueTask<bool> TryAutoloadQualifiedNameAsync(string path, CancellationToken cancellationToken)
    {
        var segments = SplitQualifiedPath(path);

        if (segments.Length < 2 || !TryBuildLibraryExportIndex(out var index))
        {
            return false;
        }

        // Longest module prefix first, so `A.B.C` prefers module `A.B` over module `A`.
        for (var length = segments.Length - 1; length >= 1; length--)
        {
            var module = string.Join('.', segments.Take(length));
            var member = segments[length];

            if (!index.TryGetValue((module, member), out var files))
            {
                continue;
            }

            if (files.Count > 1)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.require.ambiguous_export",
                    Title: $"'{module}.{member}' is exported by more than one file.",
                    Help: $"{string.Join(" and ", files)} both export it. Require the one you mean."));
            }

            // Run it exactly as a written `require` would, so caching, circular-require
            // detection and the import of its exports are the same code rather than a second
            // version of it that can drift.
            var synthesised = new RequireStatementSyntax(
                files[0],
                Imports: [],
                IsNative: false,
                Alias: null,
                Modifier: DeclarationModifier.Default,
                Span: new TextSpan(0, 0));

            await foreach (var _ in EvaluateRequireStatementAsync(
                sourceName: $"<autoload {module}.{member}>",
                sourceText: string.Empty,
                statement: synthesised,
                cancellationToken))
            {
            }

            return true;
        }

        return false;
    }

    /// <summary>Reads the library once, mapping each module's exports to the file holding them.</summary>
    private bool TryBuildLibraryExportIndex(
        out Dictionary<(string Module, string Member), List<string>> index)
    {
        if (_libraryExports is not null)
        {
            index = _libraryExports;
            return true;
        }

        index = new Dictionary<(string, string), List<string>>();

        if (LanguageRuntime.LibraryDirectoryProvider?.Invoke() is not { Length: > 0 } library ||
            !Directory.Exists(library))
        {
            return false;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetFullPath(library), "*.tosh", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                var module = ModuleDeclaration.Match(text);

                if (!module.Success)
                {
                    continue;
                }

                var owner = module.Groups[1].Value;

                foreach (Match member in ExportedMember.Matches(text))
                {
                    var key = (owner, member.Groups[1].Value);

                    if (!index.TryGetValue(key, out var files))
                    {
                        index[key] = files = [];
                    }

                    if (!files.Contains(file))
                    {
                        files.Add(file);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        _libraryExports = index;
        return true;
    }

    /// <summary>The recognised file kinds, which is also what marks a target as a path.</summary>
    private static readonly string[] RequireExtensions = [".tosh", ".dll", ".csproj"];

    /// <summary>
    /// Resolves a dotted library name, or answers null when the target is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Core.Shell</c> becomes <c>&lt;library&gt;/Core/Shell.tosh</c>, or that directory's
    /// <c>init.tosh</c> where the module has grown from one file into a folder. Finding both
    /// is reported rather than silently decided: it is what a half-finished move from the
    /// one to the other looks like, and picking either would hide it.
    /// </para>
    /// <para>
    /// A name may not climb out of the library. <c>..</c> in a require is either a mistake
    /// or an attempt to reach somewhere the reader did not mean to expose, and the path form
    /// is there for anyone who genuinely wants to name a file elsewhere.
    /// </para>
    /// </remarks>
    private RequireTarget? TryResolveLibraryName(string target)
    {
        if (LanguageRuntime.LibraryDirectoryProvider?.Invoke() is not { Length: > 0 } library ||
            !IsLibraryName(target))
        {
            return null;
        }

        var relative = target.Replace('.', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(library);
        var asFile = Path.Combine(root, relative + ".tosh");
        var asPackage = Path.Combine(root, relative, "init.tosh");

        var fileExists = File.Exists(asFile);
        var packageExists = File.Exists(asPackage);

        if (fileExists && packageExists)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.require.ambiguous_library_name",
                Title: $"'{target}' names both a file and a package in the library.",
                Help: $"'{asFile}' and '{asPackage}' both exist. Delete whichever is no longer "
                    + "the module, or require the one you mean by its path."));
        }

        if (fileExists)
        {
            return new RequireTarget(RequireTargetKind.Script, asFile, asFile);
        }

        return packageExists
            ? new RequireTarget(RequireTargetKind.Script, asPackage, asPackage)
            : null;
    }

    /// <summary>Whether a target was written as a name rather than as a path.</summary>
    private static bool IsLibraryName(string target)
    {
        if (string.IsNullOrWhiteSpace(target) ||
            target.Contains('/') ||
            target.Contains('\\') ||
            target.StartsWith('~') ||
            target.StartsWith('.') ||
            Path.IsPathRooted(target))
        {
            return false;
        }

        // An extension says "I mean a file"; `..` says nothing good.
        return !RequireExtensions.Any(extension => target.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            && !target.Split('.').Any(segment => segment.Length == 0);
    }

    private sealed record RequireTarget(RequireTargetKind Kind, string ResolvedPath, string CacheKey);
}

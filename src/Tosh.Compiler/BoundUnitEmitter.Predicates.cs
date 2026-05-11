using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics.SymbolStore;
using Tosh.Language.Binding;
using Tosh.Compiler.IR;
using Tosh.Language.Parsing;
using Tosh.Runtime;
namespace Tosh.Compiler;

internal sealed partial class EmitterImpl
{
    private bool ProgramUsesBlockExpressions()
    {
        return ContainsBlockExpression(_unit.Root);

        static bool ContainsBlockExpression(BoundNode? node)
        {
            if (node is null) return false;
            if (node is BoundBlockExpression) return true;
            var type = node.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                if (value is BoundNode child)
                {
                    if (ContainsBlockExpression(child)) return true;
                }
                else if (value is System.Collections.IEnumerable seq && value is not string)
                {
                    foreach (var item in seq)
                    {
                        if (item is BoundNode bn && ContainsBlockExpression(bn)) return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> only when the program contains at least one
    /// block expression whose body <em>cannot</em> be compiled to CLR
    /// IL — i.e. that would fall back to
    /// <see cref="EmitMakeBlockFallback"/>. When every block in the
    /// program can be compiled, the <c>RegisterSource</c> prologue is
    /// unnecessary and this returns <c>false</c>.
    /// </summary>
    private bool ProgramHasBlockExpressionsNeedingReplay()
    {
        return ContainsNonCompilableBlock(_unit.Root);

        static bool ContainsNonCompilableBlock(BoundNode? node)
        {
            if (node is null) return false;
            if (node is BoundBlockExpression block && !CanCompileBlockBody(block)) return true;
            var type = node.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                if (value is BoundNode child)
                {
                    if (ContainsNonCompilableBlock(child)) return true;
                }
                else if (value is System.Collections.IEnumerable seq && value is not string)
                {
                    foreach (var item in seq)
                    {
                        if (item is BoundNode bn && ContainsNonCompilableBlock(bn)) return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if the bound unit contains any user-defined type
    /// declaration that still relies on source replay (Tier 3).
    /// CLR-shell-emitted classes/records are excluded.
    /// </summary>
    private bool ProgramHasTypeDefinitionsNeedingReplay()
    {
        foreach (var stmt in _unit.Root.Statements)
        {
            if (TypeDefinitionNeedsSourceReplay(stmt, out _)) return true;
        }
        return false;
    }

    private bool TypeDefinitionNeedsSourceReplay(BoundStatement stmt, out TextSpan span)
    {
        if (!IsTypeDefinitionStatement(stmt, out span)) return false;
        return !IsClrShellEmittedTypeDefinition(stmt);
    }

    /// <summary>
    /// Returns true if the bound unit contains a top-level declaration
    /// that is accepted in the permissive profile only by replaying the
    /// original source through the interpreter.
    /// </summary>
    private bool ProgramHasTopLevelDeclarationsNeedingReplay()
    {
        foreach (var stmt in _unit.Root.Statements)
        {
            if (TopLevelDeclarationNeedsSourceReplay(stmt, out _)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the bound unit contains any rune (macro) definition
    /// that will be registered via the Tier-2 <c>RegisterRuneFromSource</c>
    /// bridge. The bridge slices the source text, so <c>RegisterSource</c>
    /// must be called before it.
    /// </summary>
    private bool ProgramHasRuneDefinitionsForTier2()
    {
        return _unit.Root.Statements.Any(s => s is BoundRuneDefinition);
    }

    /// <summary>
    /// Returns true if the bound unit contains any non-native require
    /// statement that is not already satisfied by a sibling compilation
    /// source. Such requires are resolved at runtime via the Tier-2
    /// <c>RequireModule</c> bridge.
    /// </summary>
    private bool ProgramHasUnsatisfiedNonNativeRequires()
    {
        return _unit.Root.Statements.Any(s =>
            s is BoundRequireStatement req &&
            !req.IsNative &&
            !RequireTargetIsSatisfiedAtBuildTime(req));
    }

    private bool TopLevelDeclarationNeedsSourceReplay(BoundStatement stmt, out TextSpan span)
    {
        switch (stmt)
        {
            case BoundFunctionDefinition fn when FunctionNeedsSourceReplay(fn):
                span = fn.Span;
                return true;
            // Native requires still need source replay: bind blocks are Tier 3
            // until first-class .NET plan step 7 is complete.
            case BoundRequireStatement require when require.IsNative:
                span = require.Span;
                return true;
            case BoundBindStatement bind when !_clrNativeBinds.Contains(bind):
                span = bind.Span;
                return true;
            default:
                span = default;
                return false;
        }
    }

    /// <summary>
    /// True when a <c>require</c> statement's target is one of the
    /// sibling sources passed to <c>BoundUnitEmitter.Emit(...)</c>.
    /// Such a require is a build-time dependency edge: the symbols
    /// it would import are already part of this assembly's
    /// compilation unit, so the runtime engine doesn't need to
    /// replay the require to make them visible. Native requires
    /// (<c>require native "libc" as LibC</c>) still need source
    /// replay because their bindings are populated by Tier-3
    /// <c>bind</c> blocks until step 7 (P/Invoke) lands.
    /// </summary>
    private bool RequireTargetIsSatisfiedAtBuildTime(BoundRequireStatement require)
    {
        if (require.IsNative) return false;
        if (_compilationSiblingKeys.Count == 0) return false;
        var target = require.Target;
        if (string.IsNullOrEmpty(target)) return false;
        return MatchesCompilationSibling(target);
    }

    private bool MatchesCompilationSibling(string target)
    {
        var trimmed = target.Trim();
        if (trimmed.Length == 0) return false;
        var keys = _compilationSiblingKeys;
        if (keys.Contains(trimmed.ToLowerInvariant())) return true;
        // Match by basename / stem so both `./common.tosh` and the
        // bare module name `common` resolve to the same sibling.
        try
        {
            var basename = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(basename) &&
                keys.Contains(basename.ToLowerInvariant()))
            {
                return true;
            }
            var stem = Path.GetFileNameWithoutExtension(trimmed);
            if (!string.IsNullOrEmpty(stem) &&
                keys.Contains(stem.ToLowerInvariant()))
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            // Path APIs throw on illegal characters; treat as
            // "not a sibling" rather than failing the build.
        }
        return false;
    }

    private static HashSet<string> BuildCompilationSiblingKeys(IReadOnlyList<string>? sources)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (sources == null) return keys;
        foreach (var path in sources)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            keys.Add(path.ToLowerInvariant());
            try
            {
                var full = Path.GetFullPath(path);
                keys.Add(full.ToLowerInvariant());
                var basename = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(basename))
                {
                    keys.Add(basename.ToLowerInvariant());
                }
                var stem = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(stem))
                {
                    keys.Add(stem.ToLowerInvariant());
                }
            }
            catch (ArgumentException)
            {
                // Path APIs throw on illegal characters; the raw
                // string still went into `keys` above, so an exact
                // match is still possible.
            }
        }
        return keys;
    }

    private bool FunctionNeedsSourceReplay(BoundFunctionDefinition func)
    {
        // Overloads are now emittable: each gets a distinct CLR
        // signature (and a fresh CLR name only when the signature
        // collides with another overload of the same tosh name).
        // Call sites resolve to the right overload via
        // BoundCommandCall.OverloadIndex stamped at lowering time.
        return false;
    }

    /// <summary>
    /// Builds a stable, canonical key describing the CLR signature
    /// the emitter is about to declare for a user function.
    /// Overloads of the same tosh name with distinct keys can share
    /// the bare CLR name (CLR allows same-name methods with
    /// different signatures); colliding keys force the legacy
    /// <c>__ov{index}</c> suffix.
    /// </summary>
    private static string BuildOverloadSignatureKey(
        bool isTyped,
        Type[] paramClrTypes,
        Type returnClr,
        bool usesPackedArguments,
        int paramCount)
    {
        if (isTyped)
        {
            var parts = new string[paramClrTypes.Length];
            for (var i = 0; i < paramClrTypes.Length; i++)
            {
                parts[i] = paramClrTypes[i].FullName ?? paramClrTypes[i].Name;
            }
            return $"T:{returnClr.FullName ?? returnClr.Name}({string.Join(',', parts)})";
        }
        if (usesPackedArguments)
        {
            return "P";
        }
        return $"U:{paramCount}";
    }

    private int TopLevelFunctionOverloadCount(string name)
    {
        var count = 0;
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundFunctionDefinition fn &&
                string.Equals(fn.Name, name, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// True if the bound unit contains any top-level <c>module</c>
    /// declaration whose body requires source replay
    /// (i.e. <see cref="ModuleNeedsSourceReplay"/> returns
    /// <see langword="true"/>). Pure-shell modules — bodies that
    /// contain only vars, CLR-emittable funcs and pure nested
    /// modules — do not register source replay and therefore do
    /// not force the source-registration prologue.
    /// </summary>
    private bool ProgramHasModuleDefinitionsNeedingReplay()
    {
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundModuleDefinition mod && ModuleNeedsSourceReplay(mod)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the program contains constructs whose invocation semantics
    /// require the interpreter to see the whole script, not just a registered
    /// declaration. Runes are the current example: definition replay registers
    /// the rune, but calls must still be expanded by the engine.
    ///
    /// First-class .NET plan, step 6 (phase 1): rune definitions on their own
    /// no longer force whole-script replay. We only fall back to it when the
    /// source text outside the rune definition spans contains a word matching
    /// one of the rune names — a possible call site that the IL emitter
    /// cannot expand without an AST-level macro pass. A future step will
    /// replace this textual call-site check with a real expansion pass that
    /// rewrites call sites at compile time.
    /// </summary>
    private bool ProgramNeedsWholeScriptReplay()
    {
        List<(string Name, TextSpan Span)>? runeDefs = null;
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundRuneDefinition rune)
            {
                runeDefs ??= new();
                runeDefs.Add((rune.Name, rune.Span));
            }
        }
        if (runeDefs is null) return false;

        // Textual scan of the source text minus the rune definition
        // spans. If any rune name appears as a whole word outside its
        // own definition, we conservatively treat the program as
        // having a rune call site and fall back to whole-script
        // replay. Definition-only programs (no callers) compile to
        // pure IL apart from the per-declaration source-replay that
        // registers the rune itself.
        var src = ((ParseResult)_unit.ParseResult).SourceText;
        if (string.IsNullOrEmpty(src)) return false;

        // Build a mask of byte positions that lie inside any rune
        // definition span. Cheap O(n) scan; runeDefs is small.
        var inDef = new bool[src.Length];
        foreach (var (_, span) in runeDefs)
        {
            var end = Math.Min(src.Length, span.Start + span.Length);
            for (var i = Math.Max(0, span.Start); i < end; i++) inDef[i] = true;
        }

        foreach (var (name, _) in runeDefs)
        {
            if (string.IsNullOrEmpty(name)) continue;
            var idx = 0;
            while (true)
            {
                idx = src.IndexOf(name, idx, StringComparison.Ordinal);
                if (idx < 0) break;
                var endIdx = idx + name.Length;
                var leftOk = idx == 0 || !IsRuneIdentChar(src[idx - 1]);
                var rightOk = endIdx >= src.Length || !IsRuneIdentChar(src[endIdx]);
                if (leftOk && rightOk && (idx >= inDef.Length || !inDef[idx]))
                {
                    return true;
                }
                idx = endIdx;
            }
        }
        return false;
    }

    private static bool IsRuneIdentChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    /// <summary>
    /// True if the bound unit declares any subcommand block or
    /// top-level script-input (<c>flag</c> / <c>arg</c>) statement.
    /// Such scripts need argv-driven dispatch, which is handled
    /// entirely by the engine; the emitter delegates the whole
    /// program to <c>ToshHost.RunSubcommandScript</c> rather than
    /// emitting per-statement IL. (Tier 3.)
    /// </summary>
    private bool ProgramHasSubcommandDispatch()
    {
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundSubcommandStatement or BoundScriptInputStatement) return true;
        }
        return false;
    }

    // ─── Compiled subcommand dispatch ─────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when all <see cref="BoundScriptInputStatement"/>
    /// parameters in the entire subcommand tree have either no default
    /// or a literal default (simple <see cref="BoundLiteral"/>), and
    /// the root level contains no regular-variable declarations that
    /// would need cross-subcommand scope threading.
    /// Falls back to source-replay otherwise.
    /// </summary>
    private bool CanCompileSubcommandDispatch()
        => CanCompileSubcommandTopLevelStatements(_unit.Root.Statements);

    // Validates that TOP-LEVEL statements (root scope) are all acceptable
    // for compiled subcommand dispatch. Subcommand bodies may contain anything.
    private static bool CanCompileSubcommandTopLevelStatements(IReadOnlyList<BoundStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                foreach (var param in input.Parameters)
                    if (param.Default is not null && !TryGetLiteralDefaultValue(param.Default, out _))
                        return false;
                continue;
            }
            if (stmt is BoundSubcommandStatement sub)
            {
                // Recursively check nested subcommand's inputs/nested-subcommands only.
                if (!CanCompileSubcommandBodyStructure(sub.Body.Statements))
                    return false;
                continue;
            }
            // Function definitions, type/module declarations, and arbitrary
            // top-level statements (var decls, pipelines, expressions, etc.)
            // are all accepted: function/type/module decls are emitted in the
            // standard pre-passes, and the rest are gathered into the
            // synthesized "__subcommand_root" body method that runs before
            // dispatch (see EmitCompiledSubcommandDispatch).
        }
        return true;
    }

    // Validates the inside of a subcommand body: only validates input declarations
    // and nested subcommands (their inputs). Body statements are arbitrary and accepted.
    private static bool CanCompileSubcommandBodyStructure(IReadOnlyList<BoundStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                foreach (var param in input.Parameters)
                    if (param.Default is not null && !TryGetLiteralDefaultValue(param.Default, out _))
                        return false;
                continue;
            }
            if (stmt is BoundSubcommandStatement sub)
            {
                if (!CanCompileSubcommandBodyStructure(sub.Body.Statements))
                    return false;
                continue;
            }
            // All other body statements (pipeline, var decl, etc.) are accepted.
        }
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="pipeline"/> reduces to a
    /// single <see cref="BoundLiteral"/> expression, extracting the
    /// literal value into <paramref name="value"/>.
    /// </summary>
    private static bool TryGetLiteralDefaultValue(BoundPipeline pipeline, out object? value)
    {
        value = null;
        if (pipeline.Stages.Count != 1) return false;
        if (pipeline.Stages[0] is not BoundExpressionStage { Value: BoundLiteral lit }) return false;
        value = lit.Value;
        return true;
    }

}

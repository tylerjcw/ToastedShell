using Tosh.Compiler.IR;
using Tosh.Language.Parsing;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using System.Reflection;

namespace Tosh.Language.Binding;

/// <summary>
/// Post-bind type-checking pass. Walks a <see cref="BoundUnit"/>
/// tree and emits <see cref="ToshDiagnostic"/> entries for the
/// shapes the new <see cref="BoundType"/> hierarchy can validate
/// statically:
///
/// <list type="bullet">
///   <item><c>tosh.type.mismatch</c> — typed assignment / return /
///     argument receives an incompatible concrete type.</item>
///   <item><c>tosh.type.arity</c> — a known user-defined function is
///     invoked with the wrong argument count.</item>
/// </list>
///
/// Anything involving <see cref="BoundType.Dynamic"/> on either side
/// is silently allowed — gradual typing leaves dynamic flow to the
/// runtime. The checker is deliberately conservative: it never
/// fabricates a diagnostic when one of the operands is unknown.
///
/// Diagnostics emerge with <see cref="ToshDiagnosticSeverity.Warning"/>
/// by default. The compile-mode driver (T3) will promote them to
/// <see cref="ToshDiagnosticSeverity.Error"/> via
/// <see cref="PromoteSeverity(ToshDiagnostic, ToshDiagnosticSeverity)"/>.
/// </summary>
public static class TypeChecker
{
    public static IReadOnlyList<ToshDiagnostic> Check(BoundUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var ctx = new CheckContext(unit);

        // `IsAssignable` is static and has no context, but deciding whether one
        // user class derives from another needs the declarations. Harvest the
        // name -> base-name edges once per check and expose them thread-locally
        // for the duration; parallel test runs each get their own map.
        _classBaseNames = new Dictionary<string, string?>(StringComparer.Ordinal);
        _classContracts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        CollectUserClasses(unit.Root, _classBaseNames, _classContracts);

        // Read from the parse tree rather than the bound one: `extend` adds no
        // bound node of its own, and the names are all this needs (`TS-P3-27`).
        if (unit.ParseResult is ParseResult parsed)
        {
            CollectExtensionMethodNames(parsed.Statement, ctx.ExtensionMethodNames);
        }

        try
        {
        // Pre-pass: harvest user-function signatures so call-site
        // arity/argument checks can fire without forward-reference
        // pain. Function bodies are walked inside this same pass —
        // there is no separate resolution phase.
        CollectUserFunctions(unit.Root, ctx.UserFunctions);
        Walk(unit.Root, ctx);
        return ctx.Diagnostics;
        }
        finally
        {
            _classBaseNames = null;
            _classContracts = null;
        }
    }

    /// <summary>
    /// name -> `extends` target, for the check currently running on this thread.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<string, string?>? _classBaseNames;

    /// <summary>
    /// name -> the interfaces it fulfills and the traits it uses, for the check
    /// currently running on this thread.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<string, HashSet<string>>? _classContracts;

    private static void CollectUserClasses(
        BoundNode node,
        Dictionary<string, string?> bases,
        Dictionary<string, HashSet<string>> contracts)
    {
        switch (node)
        {
            case BoundScript s:
                foreach (var st in s.Statements) CollectUserClasses(st, bases, contracts);
                break;
            case BoundClassDefinition cls:
                bases[cls.Name] = cls.BaseClassName;
                var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (cls.ImplementedInterfaces is { } ifaces) foreach (var i in ifaces) declared.Add(i);
                if (cls.UsedTraits is { } traits) foreach (var t in traits) declared.Add(t);
                contracts[cls.Name] = declared;
                break;
            case BoundFunctionDefinition fn:
                CollectUserClasses(fn.Body, bases, contracts);
                break;
            case BoundBlock b:
                foreach (var st in b.Statements) CollectUserClasses(st, bases, contracts);
                break;
            case BoundIfStatement i:
                CollectUserClasses(i.ThenBlock, bases, contracts);
                if (i.ElseBlock is not null) CollectUserClasses(i.ElseBlock, bases, contracts);
                break;
            case BoundForStatement f:
                CollectUserClasses(f.Body, bases, contracts);
                break;
            case BoundWhileStatement w:
                CollectUserClasses(w.Body, bases, contracts);
                break;
        }
    }

    /// <summary>
    /// True when <paramref name="className"/>, or anything it inherits from,
    /// fulfills an interface or uses a trait called <paramref name="contract"/>.
    /// </summary>
    private static bool SatisfiesContract(string className, string contract)
    {
        if (_classContracts is not { } contracts) return false;

        var current = className;

        for (var depth = 0; depth < 64; depth++)
        {
            if (contracts.TryGetValue(current, out var declared) && declared.Contains(contract)) return true;
            if (_classBaseNames is not { } bases ||
                !bases.TryGetValue(current, out var next) || next is null) return false;
            current = next;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="derivedName"/> reaches <paramref name="baseName"/>
    /// by following `extends`. Depth-capped so a malformed cycle cannot hang the
    /// checker — a cycle is a separate diagnostic's problem, not this one's.
    /// </summary>
    private static bool DerivesFrom(string derivedName, string baseName)
    {
        if (string.Equals(derivedName, baseName, StringComparison.Ordinal)) return true;
        if (_classBaseNames is not { } bases) return false;

        var current = derivedName;

        for (var depth = 0; depth < 64; depth++)
        {
            if (!bases.TryGetValue(current, out var next) || next is null) return false;
            if (string.Equals(next, baseName, StringComparison.Ordinal)) return true;
            current = next;
        }

        return false;
    }

    /// <summary>
    /// Compile-mode-only annotation audit. Walks the unit looking
    /// for <see cref="BoundFunctionDefinition"/> and
    /// <see cref="BoundVariableDeclaration"/> shapes that lack a
    /// type annotation, and emits one diagnostic per offence:
    /// <list type="bullet">
    ///   <item><c>tosh.compile.missing_type_annotation</c> — a
    ///     function parameter or return type is missing entirely.
    ///     Always an error in compile mode.</item>
    ///   <item><c>tosh.compile.implicit_dynamic</c> — a <c>var</c>
    ///     declaration was given no annotation and the inferrer
    ///     could not pin down a concrete type, or its written annotation
    ///     could not be resolved. Suppressible via
    ///     <paramref name="allowDynamic"/>.</item>
    /// </list>
    /// All emitted diagnostics carry <see cref="ToshDiagnosticSeverity.Error"/>;
    /// the caller is expected to treat them accordingly.
    /// </summary>
    public static IReadOnlyList<ToshDiagnostic> CheckCompileAnnotations(BoundUnit unit, bool allowDynamic)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var diagnostics = new List<ToshDiagnostic>();
        WalkAnnotations(unit.Root, unit, diagnostics, allowDynamic);
        return diagnostics;
    }

    private static void WalkAnnotations(
        BoundNode node,
        BoundUnit unit,
        List<ToshDiagnostic> diagnostics,
        bool allowDynamic)
    {
        switch (node)
        {
            case BoundScript s:
                foreach (var st in s.Statements) WalkAnnotations(st, unit, diagnostics, allowDynamic);
                break;
            case BoundBlock b:
                foreach (var st in b.Statements) WalkAnnotations(st, unit, diagnostics, allowDynamic);
                break;
            case BoundIfStatement i:
                WalkAnnotations(i.ThenBlock, unit, diagnostics, allowDynamic);
                if (i.ElseBlock is not null) WalkAnnotations(i.ElseBlock, unit, diagnostics, allowDynamic);
                break;
            case BoundForStatement f:
                WalkAnnotations(f.Body, unit, diagnostics, allowDynamic);
                break;
            case BoundWhileStatement w:
                WalkAnnotations(w.Body, unit, diagnostics, allowDynamic);
                break;
            case BoundFunctionDefinition fn:
                CheckFunctionAnnotations(fn, unit, diagnostics);
                WalkAnnotations(fn.Body, unit, diagnostics, allowDynamic);
                break;
            case BoundVariableDeclaration decl when !allowDynamic:
                CheckVarAnnotation(decl, unit, diagnostics);
                break;
        }
    }

    private static void CheckFunctionAnnotations(
        BoundFunctionDefinition fn,
        BoundUnit unit,
        List<ToshDiagnostic> diagnostics)
    {
        // Return type must be annotated in compile mode. Missing
        // annotation -> ReturnTypeName is null. Explicit `dynamic` /
        // `any` / `object` annotations are an opt-in and stay legal.
        if (fn.ReturnTypeName is null)
        {
            diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.compile.missing_type_annotation",
                Title: $"Function '{fn.Name}' is missing a return-type annotation.",
                SourceName: (unit.ParseResult as ParseResult)?.SourceName,
                SourceText: (unit.ParseResult as ParseResult)?.SourceText,
                Span: fn.Span,
                Help: "annotate the return type, e.g. `func " + fn.Name + "(...) -> int { ... }`. Use `dynamic` to opt out explicitly.",
                Severity: ToshDiagnosticSeverity.Error,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
        }

        for (var i = 0; i < fn.Parameters.Count; i++)
        {
            var p = fn.Parameters[i];
            if (p.Symbol.DeclaredType.IsDynamic && !ParameterIsExplicitlyDynamic(fn, i))
            {
                diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.compile.missing_type_annotation",
                    Title: $"Parameter '{p.Name}' of '{fn.Name}' is missing a type annotation.",
                    SourceName: (unit.ParseResult as ParseResult)?.SourceName,
                    SourceText: (unit.ParseResult as ParseResult)?.SourceText,
                    Span: p.Span,
                    Help: $"annotate the parameter, e.g. `{p.Name}: int`. Use `dynamic` to opt out explicitly.",
                    Severity: ToshDiagnosticSeverity.Error,
                    Category: ToshDiagnosticCategory.Type,
                    Lifecycle: ToshDiagnosticLifecycle.Preview));
            }
        }
    }

    /// <summary>
    /// True when the syntax-level parameter explicitly carries the
    /// <c>dynamic</c> annotation (the resolver reduces both
    /// <c>dynamic</c> and absent annotations to
    /// <see cref="BoundType.Dynamic"/>, so we have to peek at the
    /// captured <see cref="BoundParameter.TypeName"/> to tell them
    /// apart).
    /// </summary>
    private static bool ParameterIsExplicitlyDynamic(BoundFunctionDefinition fn, int index)
    {
        var name = fn.Parameters[index].TypeName;
        if (string.IsNullOrEmpty(name)) return false;
        var trimmed = name.Trim();
        return string.Equals(trimmed, "dynamic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "any", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "object", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckVarAnnotation(
        BoundVariableDeclaration decl,
        BoundUnit unit,
        List<ToshDiagnostic> diagnostics)
    {
        // Explicit `: dynamic` is an intentional opt-out and must
        // not be reported as implicit dynamic.
        if (decl.AnnotatedDynamic) return;

        // An unresolved annotation may have a concrete initializer. Lowering retains that
        // inferred implementation type as a best effort, but it must not erase the failed
        // source-level contract from the compile audit.
        if (!decl.HasUnresolvedTypeAnnotation && !decl.Symbol.DeclaredType.IsDynamic) return;

        // `TOAST-0076`. An annotation that failed to resolve is a different report from no
        // annotation at all. Telling a reader who wrote `var v: M.Box` that the variable
        // "has no type annotation", and advising them to add one, describes neither what
        // happened nor anything they can do — the obvious reply is "but I did", and the real
        // cause appears nowhere.
        var annotated = decl.HasUnresolvedTypeAnnotation;
        var annotationName = decl.Symbol.DeclaredTypeName;

        diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.compile.implicit_dynamic",
            Title: annotated
                ? $"Variable '{decl.Symbol.Name}' is annotated "
                    + (string.IsNullOrEmpty(annotationName) ? "with a type" : $"'{annotationName}'")
                    + " but the annotation could not be resolved to a concrete type."
                : $"Variable '{decl.Symbol.Name}' has no type annotation and the inferrer could not pin down a concrete type.",
            SourceName: (unit.ParseResult as ParseResult)?.SourceName,
            SourceText: (unit.ParseResult as ParseResult)?.SourceText,
            Span: decl.Span,
            Help: annotated
                ? "check the type name is spelled correctly and is reachable from here, or pass "
                    + "`--compile-allow-dynamic` to allow implicit dynamic."
                : "annotate the variable (e.g. `var " + decl.Symbol.Name + ": int = ...`) or pass `--compile-allow-dynamic` to allow implicit dynamic.",
            Severity: ToshDiagnosticSeverity.Error,
            Category: ToshDiagnosticCategory.Type,
            Lifecycle: ToshDiagnosticLifecycle.Preview));
    }

    /// <summary>
    /// Convenience for callers (compile mode, strict CI runners) that
    /// want all type-check warnings flipped to errors.
    /// </summary>
    public static ToshDiagnostic PromoteSeverity(ToshDiagnostic diagnostic, ToshDiagnosticSeverity severity) =>
        diagnostic with { Severity = severity };

    // ── walker ────────────────────────────────────────────────

    private sealed class CheckContext(BoundUnit unit)
    {
        public BoundUnit Unit { get; } = unit;
        public List<ToshDiagnostic> Diagnostics { get; } = new();
        /// <summary>
        /// The `-> void` function being walked, if any — <c>TOAST-0046</c>.
        /// </summary>
        /// <remarks>
        /// Cleared when a nested function or lambda is entered: an `echo` inside a callback
        /// declared within a void function belongs to the callback, and flagging it would be
        /// blaming the wrong declaration.
        /// </remarks>
        public string? VoidFunctionName { get; set; }

        public Dictionary<string, BoundFunctionDefinition> UserFunctions { get; } =
            new(StringComparer.Ordinal);
        public BoundType? CurrentReturnType { get; set; }

        /// <summary>
        /// Resolves member type names against this unit's own declarations — `TOAST-0038`.
        /// </summary>
        /// <remarks>
        /// Built lazily and once. Without the unit's types a name like `Node` resolves
        /// through the platform index to whatever CLR type shares it, and every annotation
        /// naming a user type reports a mismatch against itself.
        /// </remarks>
        public TypeNameResolver MemberTypeResolver => _memberTypeResolver ??= new TypeNameResolver(
            userTypes: Unit.ParseResult is ParseResult parsed
                ? Lowerer.BuildUserTypeRegistry(parsed.Statement)
                : null);

        private TypeNameResolver? _memberTypeResolver;

        public string? SourceName => (Unit.ParseResult as ParseResult)?.SourceName;
        public string? SourceText => (Unit.ParseResult as ParseResult)?.SourceText;

        /// <summary>
        /// Method names declared by `extend` blocks in this source.
        /// </summary>
        /// <remarks>
        /// A method reached through an extension is not on the type, so the
        /// member-not-found check has to know about them or it warns about every
        /// extension call (`TS-P3-27`). Names only, and not per-type: extensions also
        /// arrive with imported modules, which are not in this unit, so a check keyed
        /// on the receiver's type would still be wrong for those — and a warning that
        /// is right about the type but wrong about the import is no better than one
        /// that is simply quiet. Being quiet about a name somebody extended somewhere
        /// is the smaller error.
        /// </remarks>
        public HashSet<string> ExtensionMethodNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gathers the method names every <c>extend</c> block declares.</summary>
    private static void CollectExtensionMethodNames(StatementSyntax? statement, HashSet<string> sink)
    {
        switch (statement)
        {
            case null:
                return;

            case ExtendStatementSyntax extend:
                foreach (var member in extend.Members)
                {
                    if (member is ClassMethodMemberSyntax method)
                    {
                        sink.Add(method.Method.Name);
                    }
                }

                return;

            case ScriptStatementSyntax script:
                foreach (var child in script.Statements)
                {
                    CollectExtensionMethodNames(child, sink);
                }

                return;

            case ModuleDefinitionStatementSyntax module:
                foreach (var child in module.Body.Statements)
                {
                    CollectExtensionMethodNames(child, sink);
                }

                return;
        }
    }

    private static void CollectUserFunctions(
        BoundNode node,
        Dictionary<string, BoundFunctionDefinition> sink)
    {
        switch (node)
        {
            case BoundScript s:
                foreach (var st in s.Statements) CollectUserFunctions(st, sink);
                break;
            case BoundFunctionDefinition fn:
                sink[fn.Name] = fn;
                CollectUserFunctions(fn.Body, sink);
                break;
            case BoundBlock b:
                foreach (var st in b.Statements) CollectUserFunctions(st, sink);
                break;
            case BoundIfStatement i:
                CollectUserFunctions(i.ThenBlock, sink);
                if (i.ElseBlock is not null) CollectUserFunctions(i.ElseBlock, sink);
                break;
            case BoundForStatement f:
                CollectUserFunctions(f.Body, sink);
                break;
            case BoundWhileStatement w:
                CollectUserFunctions(w.Body, sink);
                break;
        }
    }

    private static void Walk(BoundNode node, CheckContext ctx)
    {
        switch (node)
        {
            case BoundScript s:
                foreach (var st in s.Statements) Walk(st, ctx);
                break;

            case BoundBlock b:
                foreach (var st in b.Statements) Walk(st, ctx);
                break;

            case BoundFunctionDefinition fn:
                {
                    var prev = ctx.CurrentReturnType;
                    var prevVoid = ctx.VoidFunctionName;
                    ctx.CurrentReturnType = fn.ReturnType;

                    // `TOAST-0046`. `void` and `nothing` are one bound type, so both arrive
                    // here as `BoundTypeKind.Void` and neither can behave differently from
                    // the other by construction.
                    ctx.VoidFunctionName = fn.ReturnType.Kind == BoundTypeKind.Void ? fn.Name : null;

                    Walk(fn.Body, ctx);
                    ctx.CurrentReturnType = prev;
                    ctx.VoidFunctionName = prevVoid;
                }
                break;

            case BoundVariableDeclaration decl:
                CheckVariableDeclaration(decl, ctx);
                if (decl.Value is not null) CheckPipeline(decl.Value, ctx);
                break;

            case BoundReturnStatement ret:
                if (ret.Value is not null)
                {
                    ReportVoidProduces(ctx, "return a value", ret.Span);
                }

                CheckReturn(ret, ctx);
                break;

            case BoundYieldStatement yield when yield.Value is not null:
                ReportVoidProduces(ctx, "yield a value", yield.Span);
                break;

            case BoundIfStatement i:
                CheckCondition(i.Condition, i.Condition.Span, "if", ctx);
                Walk(i.ThenBlock, ctx);
                if (i.ElseBlock is not null) Walk(i.ElseBlock, ctx);
                break;

            case BoundForStatement f:
                CheckPipeline(f.Source, ctx);
                Walk(f.Body, ctx);
                break;

            case BoundWhileStatement w:
                CheckCondition(w.Condition, w.Condition.Span, w.IsUntil ? "until" : "while", ctx);
                Walk(w.Body, ctx);
                break;

            case BoundBlockStatement blockStatement:
                Walk(blockStatement.Body, ctx);
                break;

            case BoundPipelineStatement ps:
                CheckPipeline(ps.Pipeline, ctx);
                break;

            case BoundVariableAssignment va:
                CheckPipeline(va.Value, ctx);
                break;

            case BoundMemberAssignment ma:
                WalkExpression(ma.Target, ctx);
                CheckPipeline(ma.Value, ctx);
                CheckUserMemberAssignment(ma, ctx);
                break;

            case BoundTupleAssignment ta:
                CheckPipeline(ta.Value, ctx);
                break;

            case BoundDestructuringDeclaration dd:
                CheckPipeline(dd.Value, ctx);
                break;

            case BoundAllocStatement alloc:
                CheckPipeline(alloc.Value, ctx);
                break;

            case BoundTryStatement ts:
                Walk(ts.TryBlock, ctx);
                if (ts.Catch is not null) Walk(ts.Catch.Body, ctx);
                if (ts.Finally is not null) Walk(ts.Finally, ctx);
                break;

            case BoundSwitchStatement sw:
                WalkExpression(sw.Value, ctx);
                foreach (var c in sw.Cases)
                {
                    WalkExpression(c.Pattern, ctx);
                    if (c.Guard is not null) CheckCondition(c.Guard, c.Guard.Span, "switch case guard", ctx);
                    Walk(c.Body, ctx);
                }
                if (sw.Default is not null) Walk(sw.Default, ctx);
                break;

            case BoundThrowStatement thr:
                if (thr.Value is not null) CheckPipeline(thr.Value, ctx);
                break;

            case BoundYieldStatement ys:
                if (ys.Value is not null) CheckPipeline(ys.Value, ctx);
                break;

            case BoundModuleDefinition mod:
                Walk(mod.Body, ctx);
                break;

            case BoundSubcommandStatement sub:
                Walk(sub.Body, ctx);
                break;

            case BoundScriptInputStatement scriptInput:
                foreach (var p in scriptInput.Parameters)
                    if (p.Default is not null) CheckPipeline(p.Default, ctx);
                break;

            case BoundClassDefinition cls:
                foreach (var member in cls.Members)
                {
                    switch (member)
                    {
                        case BoundClassPropertyMember prop:
                            if (prop.Initializer is not null) CheckPipeline(prop.Initializer, ctx);
                            CheckMemberAnnotation(prop, ctx);
                            if (prop.GetterBody is not null) Walk(prop.GetterBody, ctx);
                            if (prop.SetterBody is not null) Walk(prop.SetterBody, ctx);
                            break;
                        case BoundClassMethodMember m:
                            Walk(m.Method, ctx);
                            break;
                        case BoundClassConstructorMember ctor:
                            foreach (var p in ctor.Parameters)
                                if (p.Default is not null) CheckPipeline(p.Default, ctx);
                            Walk(ctor.Body, ctx);
                            break;
                    }
                }
                break;

            case BoundRecordDefinition rec:
                foreach (var f in rec.Fields)
                    if (f.DefaultValue is not null) CheckPipeline(f.DefaultValue, ctx);
                break;

            case BoundStructDefinition st:
                foreach (var f in st.Fields)
                    if (f.DefaultValue is not null) CheckPipeline(f.DefaultValue, ctx);
                foreach (var member in st.Members)
                {
                    if (member is BoundClassPropertyMember prop)
                    {
                        if (prop.Initializer is not null) CheckPipeline(prop.Initializer, ctx);
                        CheckMemberAnnotation(prop, ctx);
                        if (prop.GetterBody is not null) Walk(prop.GetterBody, ctx);
                        if (prop.SetterBody is not null) Walk(prop.SetterBody, ctx);
                    }
                    else if (member is BoundClassMethodMember m)
                    {
                        Walk(m.Method, ctx);
                    }
                    else if (member is BoundClassConstructorMember ctor)
                    {
                        foreach (var p in ctor.Parameters)
                            if (p.Default is not null) CheckPipeline(p.Default, ctx);
                        Walk(ctor.Body, ctx);
                    }
                }
                break;

            case BoundEventDefinition ev:
                foreach (var f in ev.Fields)
                    if (f.DefaultValue is not null) CheckPipeline(f.DefaultValue, ctx);
                break;
        }
    }

    /// <summary>
    /// Reports a `-> void` function producing a value — <c>TOAST-0046</c>.
    /// </summary>
    /// <remarks>
    /// The C# rule, in a language where output is the return value: a void function may not
    /// say what it evaluates to. Only what is syntactically visible is caught — `echo`, an
    /// explicit `yield`, and `return expr`. A command whose own output happens to be
    /// non-empty cannot be recognised here, and is caught when it runs.
    /// </remarks>
    private static void ReportVoidProduces(CheckContext ctx, string what, TextSpan span)
    {
        if (ctx.VoidFunctionName is not { } name)
        {
            return;
        }

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.compile.void_function_produces_output",
            Title: $"Function '{name}' returns 'void' and cannot {what}.",
            SourceName: (ctx.Unit.ParseResult as ParseResult)?.SourceName,
            SourceText: (ctx.Unit.ParseResult as ParseResult)?.SourceText,
            Span: span,
            Label: $"'{name}' declares that it produces nothing",
            Help: "use 'writeline' to print without producing a value, or give the function a return type.",
            Severity: ToshDiagnosticSeverity.Error,
            Category: ToshDiagnosticCategory.Type,
            Lifecycle: ToshDiagnosticLifecycle.Preview));
    }

    private static void CheckPipeline(BoundPipeline pipeline, CheckContext ctx)
    {
        BoundType? previousStageOutput = null;
        for (var i = 0; i < pipeline.Stages.Count; i++)
        {
            var stage = pipeline.Stages[i];
            if (stage is BoundCommandCall call)
            {
                // `TOAST-0046`. `echo` emits a pipeline value, and a function's output *is*
                // its value here — so an `echo` in a `-> void` body is the shell's version
                // of `return expr;` in a C# void method. `writeline` writes straight to the
                // console and yields nothing, which is what a void function prints with.
                if (string.Equals(call.Name, "echo", StringComparison.Ordinal))
                {
                    ReportVoidProduces(ctx, "echo a value", call.Span);
                }

                // A stage after the first receives its subject from the pipe,
                // so one declared argument is already supplied.
                CheckCommandCall(call, ctx, receivesPipedInput: i > 0);

                if (i > 0)
                {
                    CheckPipelineInputCompatibility(call, previousStageOutput, ctx);
                }

                previousStageOutput = TypeInferrer.InferCommandOutput(call);
            }
            else if (stage is BoundExpressionStage exprStage)
            {
                WalkExpression(exprStage.Value, ctx);
                previousStageOutput = exprStage.Value.Type;
            }
        }
    }

    private static void CheckCondition(BoundExpression condition, TextSpan span, string context, CheckContext ctx)
    {
        WalkExpression(condition, ctx);
    }

    private static void WalkExpression(BoundExpression expression, CheckContext ctx)
    {
        switch (expression)
        {
            case BoundLiteral:
            case BoundVariableReference:
            case BoundStaticMemberAccess:
            case BoundNameOfExpression:
            case BoundFunctionReference:
            case BoundQuoteExpression:
                return;

            case BoundMemberAccess ma:
                WalkExpression(ma.Target, ctx);
                CheckMemberAccess(ma, ctx);
                return;

            case BoundBinaryOperator bin:
                WalkExpression(bin.Left, ctx);
                WalkExpression(bin.Right, ctx);
                CheckBinaryOperator(bin, ctx);
                return;

            case BoundUnaryOperator un:
                WalkExpression(un.Operand, ctx);
                CheckUnaryOperator(un, ctx);
                return;

            case BoundRange range:
                WalkExpression(range.Start, ctx);
                if (range.Step is not null) WalkExpression(range.Step, ctx);
                if (range.End is not null) WalkExpression(range.End, ctx);
                return;

            case BoundArrayLiteral arr:
                foreach (var item in arr.Items) WalkExpression(item.Value, ctx);
                return;

            case BoundInterpolatedString interp:
                foreach (var part in interp.Parts)
                    if (part is BoundInterpolatedExpression ie && ie.Expression is not null)
                        WalkExpression(ie.Expression, ctx);
                return;

            case BoundConditional cond:
                CheckCondition(cond.Condition, cond.Condition.Span, "conditional", ctx);
                WalkExpression(cond.WhenTrue, ctx);
                WalkExpression(cond.WhenFalse, ctx);
                return;

            case BoundIfExpression ifExpr:
                CheckCondition(ifExpr.Condition, ifExpr.Condition.Span, "if-expression", ctx);
                Walk(ifExpr.ThenBlock, ctx);
                Walk(ifExpr.ElseBlock, ctx);
                return;

            case BoundBlockExpression be:
                Walk(be.Body, ctx);
                return;

            case BoundLambda lambda:
                foreach (var p in lambda.Parameters)
                    if (p.Default is not null) CheckPipeline(p.Default, ctx);
                Walk(lambda.Body, ctx);
                return;

            case BoundCallableInvocation inv:
                WalkExpression(inv.Target, ctx);
                foreach (var arg in inv.Arguments) WalkExpression(arg.Value, ctx);
                return;

            case BoundThrowExpression te:
                if (te.Value is not null) WalkExpression(te.Value, ctx);
                return;

            case BoundMatchExpression match:
                WalkExpression(match.Value, ctx);
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is not null) WalkExpression(arm.Pattern, ctx);
                    if (arm.Guard is not null) CheckCondition(arm.Guard, arm.Guard.Span, "match arm guard", ctx);
                    Walk(arm.Body, ctx);
                }
                return;

            case BoundNewObject no:
                foreach (var arg in no.Arguments) WalkExpression(arg.Value, ctx);
                CheckNewObject(no, ctx);
                return;

            case BoundMethodCall mc:
                WalkExpression(mc.Target, ctx);
                foreach (var arg in mc.Arguments) WalkExpression(arg.Value, ctx);
                CheckMethodCall(mc, ctx);
                return;

            case BoundStaticMethodCall smc:
                foreach (var arg in smc.Arguments) WalkExpression(arg.Value, ctx);
                return;

            case BoundIndexAccess idx:
                WalkExpression(idx.Target, ctx);
                WalkExpression(idx.Index, ctx);
                CheckIndexAccess(idx, ctx);
                return;

            case BoundRecordLiteral rl:
                foreach (var e in rl.Fields)
                {
                    if (e is BoundRecordField field) WalkExpression(field.Value, ctx);
                    else if (e is BoundRecordSpreadEntry spread) WalkExpression(spread.Value, ctx);
                    else if (e is BoundComputedRecordField computed)
                    {
                        WalkExpression(computed.NameExpression, ctx);
                        WalkExpression(computed.Value, ctx);
                    }
                }
                return;

            case BoundDictLiteral dl:
                foreach (var item in dl.Entries)
                {
                    WalkExpression(item.Key, ctx);
                    WalkExpression(item.Value, ctx);
                }
                return;

            case BoundSetLiteral sl:
                foreach (var item in sl.Items) WalkExpression(item, ctx);
                return;

            case BoundTupleLiteral tl:
                foreach (var item in tl.Items) WalkExpression(item, ctx);
                return;

            case BoundSubexpression sub:
                CheckPipeline(sub.Pipeline, ctx);
                return;

            case BoundCommandSubstitution csub:
                CheckPipeline(csub.Pipeline, ctx);
                return;

            case BoundInputProcessSubstitution ip:
                CheckPipeline(ip.Pipeline, ctx);
                return;

            case BoundOutputProcessSubstitution op:
                CheckPipeline(op.Pipeline, ctx);
                return;

            case BoundMemberProjection:
            case BoundComparisonPattern:
            case BoundDynamicExpression:
                return;
        }
    }

    private static void CheckCommandCall(
        BoundCommandCall call,
        CheckContext ctx,
        bool receivesPipedInput = false)
    {
        foreach (var arg in call.Arguments)
            WalkExpression(arg.Value, ctx);

        if (ctx.UserFunctions.TryGetValue(call.Name, out var fn))
        {
            CheckUserFunctionCommandCall(call, fn, ctx);
            return;
        }

        CheckBuiltinCommandCall(call, ctx, receivesPipedInput);
    }

    private static void CheckUserFunctionCommandCall(BoundCommandCall call, BoundFunctionDefinition fn, CheckContext ctx)
    {

        // Strip splatted / named arguments — the checker can't reason
        // about those statically. Conservative: if any non-positional
        // argument is present, skip the call entirely.
        var positionals = new List<BoundArgument>(call.Arguments.Count);
        foreach (var a in call.Arguments)
        {
            if (a.IsSplat || a.Name is not null) return;
            positionals.Add(a);
        }

        // Required parameters: those that are not optional and not rest.
        var required = 0;
        var maxAccepted = fn.Parameters.Count;
        var hasRest = false;
        foreach (var p in fn.Parameters)
        {
            if (p.IsRest) { hasRest = true; continue; }
            if (!p.IsOptional) required++;
        }
        if (hasRest) maxAccepted = int.MaxValue;

        if (positionals.Count < required || positionals.Count > maxAccepted)
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.arity",
                Title: $"Function '{call.Name}' expects {DescribeArity(required, maxAccepted)} but received {positionals.Count}.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: call.NameSpan,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
            return;
        }

        // Argument-type compat. Walk paired params/args up to the
        // shorter of the two; rest parameters are not checked
        // structurally yet.
        var pairCount = Math.Min(positionals.Count, fn.Parameters.Count);
        for (var i = 0; i < pairCount; i++)
        {
            var param = fn.Parameters[i];
            if (param.IsRest) break;
            var declared = param.Symbol.DeclaredType;
            if (declared.IsDynamic) continue;
            var actual = positionals[i].Value.Type;

            // `TS-P2-84`. A bareword in command position is untyped text that the annotation
            // converts on the way in — `bigFiles 512b` passes the word `512b` and receives a
            // `StorageSize`, and runs correctly. Typing it `String` and comparing structurally
            // reported four of the false positives an editor was showing against working
            // scripts. A word is only ever a word here; what it becomes is the annotation's
            // business, and the runtime's conversion is not modelled by this pass.
            if (IsBarewordText(positionals[i].Value)) continue;

            if (!IsAssignable(actual, declared, out var reason))
            {
                ctx.Diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.type.mismatch",
                    Title: $"Argument {i + 1} of '{call.Name}' expects '{declared.DisplayName}' but received '{actual.DisplayName}'.",
                    SourceName: ctx.SourceName,
                    SourceText: ctx.SourceText,
                    Span: positionals[i].Span,
                    Help: reason,
                    Severity: ToshDiagnosticSeverity.Warning,
                    Category: ToshDiagnosticCategory.Type,
                    Lifecycle: ToshDiagnosticLifecycle.Preview));
            }
        }
    }

    /// <summary>
    /// True when an argument is a bare word rather than a typed expression — <c>TS-P2-84</c>.
    /// </summary>
    /// <remarks>
    /// A quoted string is a string and is checked; a bareword is shell text whose meaning is
    /// decided by the parameter it lands on, so it carries no type worth comparing.
    /// </remarks>
    private static bool IsBarewordText(BoundExpression value) =>
        value is BoundLiteral { Value: string, IsBareword: true };

    private static void CheckBuiltinCommandCall(
        BoundCommandCall call,
        CheckContext ctx,
        bool receivesPipedInput)
    {
        if (call.ResolvedCommand is null) return;

        // Keep the pass conservative around splats/named arguments for now.
        foreach (var a in call.Arguments)
            if (a.IsSplat || a.Name is not null)
                return;

        var expectedArgs = call.ResolvedCommand.GetType()
            .GetCustomAttributes<CommandArgumentAttribute>(inherit: false)
            .ToArray();
        if (expectedArgs.Length == 0) return;

        if (!TryCollectBuiltinPositionals(call, ctx, expectedArgs, out var positionals))
            return;

        // `$ch | channel-recv` supplies the channel through the pipe, so the
        // command legitimately has no positional argument. Counting only what is
        // written warned on the ordinary way to use every subject-taking command.
        var required = expectedArgs.Count(a => a.Required);
        if (receivesPipedInput && required > 0)
        {
            required--;
        }

        var maxAccepted = expectedArgs.Any(IsVariadicCommandArgument)
            ? int.MaxValue
            : expectedArgs.Length;
        var provided = positionals.Count;
        if (provided < required || provided > maxAccepted)
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.command_arity",
                Title: $"Command '{call.Name}' expects {DescribeArity(required, maxAccepted)} but received {provided}.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: call.NameSpan,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
            return;
        }

        var pairCount = Math.Min(provided, expectedArgs.Length);
        for (var i = 0; i < pairCount; i++)
        {
            var expected = InferExpectedType(expectedArgs[i]);
            if (expected is null || expected.IsDynamic) continue;
            var actual = positionals[i].Value.Type;
            if (!IsAssignable(actual, expected, out var reason))
            {
                ctx.Diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.type.command_argument",
                    Title: $"Command '{call.Name}' argument {i + 1} expects '{expected.DisplayName}' but received '{actual.DisplayName}'.",
                    SourceName: ctx.SourceName,
                    SourceText: ctx.SourceText,
                    Span: positionals[i].Span,
                    Help: reason,
                    Severity: ToshDiagnosticSeverity.Warning,
                    Category: ToshDiagnosticCategory.Type,
                    Lifecycle: ToshDiagnosticLifecycle.Preview));
            }
        }
    }

    private sealed record CommandOptionShape(string Name, bool RequiresValue, bool AllowsOptionalValue);

    private static bool TryCollectBuiltinPositionals(BoundCommandCall call, CheckContext ctx, CommandArgumentAttribute[] expectedArgs, out List<BoundArgument> positionals)
    {
        positionals = new List<BoundArgument>(call.Arguments.Count);
        var optionShapes = call.ResolvedCommand!.GetType()
            .GetCustomAttributes<CommandOptionAttribute>(inherit: false)
            .SelectMany(BuildOptionShapes)
            .ToArray();

        // Find the index (in expectedArgs) at which positional parsing
        // becomes passthrough — once that many positionals have been
        // collected, every remaining token is treated as an opaque
        // positional.
        var passthroughAt = -1;
        for (var i = 0; i < expectedArgs.Length; i++)
        {
            if (expectedArgs[i].Passthrough) { passthroughAt = i; break; }
        }

        var parseOptions = true;
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var argument = call.Arguments[index];

            if (!parseOptions || !TryGetLiteralString(argument.Value, out var text))
            {
                positionals.Add(argument);
                if (passthroughAt >= 0 && positionals.Count > passthroughAt)
                    parseOptions = false;
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            if (!LooksLikeOptionToken(text))
            {
                positionals.Add(argument);
                if (passthroughAt >= 0 && positionals.Count > passthroughAt)
                    parseOptions = false;
                continue;
            }

            if (optionShapes.Length == 0)
            {
                return false;
            }

            if (!TryMatchOption(text, optionShapes, out var shape, out var valueIsAttached))
            {
                ctx.Diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.type.unknown_option",
                    Title: $"Command '{call.Name}' has no option '{text}'.",
                    SourceName: ctx.SourceName,
                    SourceText: ctx.SourceText,
                    Span: argument.Span,
                    Help: BuildKnownOptionsHelp(optionShapes),
                    Severity: ToshDiagnosticSeverity.Warning,
                    Category: ToshDiagnosticCategory.Type,
                    Lifecycle: ToshDiagnosticLifecycle.Preview));
                return false;
            }

            if (shape.AllowsOptionalValue &&
                !valueIsAttached &&
                index + 1 < call.Arguments.Count &&
                (!TryGetLiteralString(call.Arguments[index + 1].Value, out var nextText) ||
                 !LooksLikeOptionToken(nextText)))
            {
                return false;
            }

            if (shape.RequiresValue && !valueIsAttached)
            {
                if (index + 1 >= call.Arguments.Count)
                    return false;

                index++;
            }
        }

        return true;
    }

    private static string BuildKnownOptionsHelp(IReadOnlyList<CommandOptionShape> shapes)
    {
        var names = shapes.Select(s => s.Name).Distinct(StringComparer.Ordinal).Take(8);
        return "known options: " + string.Join(", ", names);
    }

    private static IEnumerable<CommandOptionShape> BuildOptionShapes(CommandOptionAttribute attribute)
    {
        var aliasGroupRequiresValue = !attribute.IsFlag && attribute.Syntax.Contains('<', StringComparison.Ordinal);
        var aliasGroupAllowsOptionalValue = !attribute.IsFlag && attribute.Syntax.Contains(" [", StringComparison.Ordinal);

        foreach (var rawVariant in attribute.Syntax.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var variant = rawVariant.Trim();
            if (variant.Length == 0) continue;

            var firstWhitespace = variant.IndexOfAny([' ', '\t']);
            var token = firstWhitespace >= 0 ? variant[..firstWhitespace] : variant;
            var bracketIndex = token.IndexOf('[');
            if (bracketIndex >= 0) token = token[..bracketIndex];
            var equalsIndex = token.IndexOf('=');
            if (equalsIndex >= 0) token = token[..equalsIndex];

            if (!LooksLikeOptionToken(token)) continue;

            var requiresValue = !attribute.IsFlag && (variant.Contains(" <", StringComparison.Ordinal) || aliasGroupRequiresValue);
            var optionalValue = !attribute.IsFlag && (variant.Contains(" [", StringComparison.Ordinal) || aliasGroupAllowsOptionalValue);
            yield return new CommandOptionShape(token, requiresValue, optionalValue);
        }
    }

    private static bool TryMatchOption(
        string text,
        IReadOnlyList<CommandOptionShape> optionShapes,
        out CommandOptionShape shape,
        out bool valueIsAttached)
    {
        valueIsAttached = false;

        if (text.StartsWith("--", StringComparison.Ordinal))
        {
            var equalsIndex = text.IndexOf('=');
            var name = equalsIndex >= 0 ? text[..equalsIndex] : text;
            shape = optionShapes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))!;
            if (shape is null) return false;
            valueIsAttached = equalsIndex >= 0;
            return true;
        }

        shape = optionShapes.FirstOrDefault(s => string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase))!;
        if (shape is not null) return true;

        if (text.Length > 2 && text[0] == '-' && text[1] != '-')
        {
            var prefix = text[..2];
            shape = optionShapes.FirstOrDefault(s =>
                string.Equals(s.Name, prefix, StringComparison.OrdinalIgnoreCase) &&
                s.RequiresValue)!;
            if (shape is not null)
            {
                valueIsAttached = true;
                return true;
            }

            foreach (var flag in text[1..])
            {
                var flagName = $"-{flag}";
                var flagShape = optionShapes.FirstOrDefault(s =>
                    string.Equals(s.Name, flagName, StringComparison.OrdinalIgnoreCase) &&
                    !s.RequiresValue);
                if (flagShape is null)
                {
                    shape = null!;
                    return false;
                }
            }

            shape = new CommandOptionShape(text, RequiresValue: false, AllowsOptionalValue: false);
            return true;
        }

        shape = null!;
        return false;
    }

    private static bool TryGetLiteralString(BoundExpression expression, out string text)
    {
        if (expression is BoundLiteral { Value: string value })
        {
            text = value;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool LooksLikeOptionToken(string text) =>
        text.Length > 1 && text[0] == '-';

    private static bool IsVariadicCommandArgument(CommandArgumentAttribute argument) =>
        argument.Variadic ||
        argument.Name.Contains("...", StringComparison.Ordinal) ||
        argument.Description.Contains("one or more", StringComparison.OrdinalIgnoreCase) ||
        argument.Description.Contains("zero or more", StringComparison.OrdinalIgnoreCase);

    private static BoundType? InferExpectedType(CommandArgumentAttribute argument)
    {
        if (argument.ClrType is { } clr)
            return BoundType.FromClr(clr);

        var kind = argument.Kind ?? InferKindFromTypeName(argument.TypeName);
        return kind?.ToLowerInvariant() switch
        {
            "path" => BoundType.FromClr(typeof(string)),
            _ => BoundType.Dynamic,
        };
    }

    private static string? InferKindFromTypeName(string? typeName) => typeName?.ToLowerInvariant() switch
    {
        null => null,
        "path-like" or "path" => "path",
        "block|callable" or "block" or "callable" => "block",
        "string" => "string",
        var t when t.Contains("expression", StringComparison.Ordinal) => "expression",
        _ => "any"
    };

    private static void CheckPipelineInputCompatibility(BoundCommandCall call, BoundType? previousOutput, CheckContext ctx)
    {
        if (call.ResolvedCommand is null || previousOutput is null) return;
        if (previousOutput.IsDynamic) return;

        var pipeAttr = call.ResolvedCommand.GetType().GetCustomAttribute<PipelineInputAttribute>(inherit: false);
        if (pipeAttr is null) return;

        var shape = previousOutput is StreamType st ? st.Element : previousOutput;

        var isListLike = shape is ListType or ArrayType || (shape.ClrType?.IsArray ?? false);
        var isRecord = shape is DictType or UserClassType or UserRecordType or UserStructType;
        var isScalar = !isListLike && !isRecord;

        // `TS-P2-84`. A list piped forward is *enumerated*: the command downstream receives the
        // elements, not the list, so what it declares about lists says nothing about whether the
        // pipe is valid. Judging the list shape against `AcceptsList` reported `each`, `where` and
        // `count` as refusing input they handle perfectly well — three of the false positives an
        // editor was showing against scripts that run. The shape cannot be judged here, so it is
        // not judged.
        if (isListLike) return;

        var accepts = (isRecord && pipeAttr.AcceptsRecord)
            || (isScalar && pipeAttr.AcceptsScalar);

        if (!accepts)
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.pipeline_input",
                Title: $"Command '{call.Name}' does not accept pipeline input of type '{previousOutput.DisplayName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: call.NameSpan,
                Help: pipeAttr.Description,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
        }
    }

    /// <summary>
    /// Whether a member missing from <paramref name="type"/> is genuinely missing at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a type that cannot be widened answers this question honestly. If the static type is an
    /// interface or an open (non-sealed) class, the value may be a more derived type at runtime
    /// that declares the member, so "not found here" is not "not found". `var a = []` infers
    /// <c>IList</c>, the value is really an <c>object[]</c>, and <c>$a.Length</c> warned
    /// <c>member_not_found</c> and then evaluated correctly (<c>TS-P2-45</c>).
    /// </para>
    /// <para>
    /// Reflection compounds it: <see cref="Type.GetMember(string, BindingFlags)"/> on an interface
    /// does not return members inherited from its base interfaces, so even the correct spelling —
    /// <c>Count</c>, which <c>IList</c> gets from <c>ICollection</c> — would have been reported
    /// missing. Two mechanisms, one symptom.
    /// </para>
    /// <para>
    /// Warning on valid code is the specific harm <c>TS-P2-41</c> was reverted for: it teaches
    /// readers to ignore the diagnostic class, which costs more than the check earns. Sealed
    /// classes, value types and arrays stay checked, which is where the typo-catching value is —
    /// <c>string</c> is sealed, so <c>$s.Trimm()</c> is still caught.
    /// </para>
    /// </remarks>
    private static bool MemberChecksAreSound(Type type)
    {
        // `TS-P2-84`. A type whose members are decided at runtime cannot be checked against its
        // declaration. `ExpandoObject` is sealed, so it passed the test below and every member of
        // a dynamic record — `$r.Name` on `{| Name = "a" |}` — was reported missing. Dictionaries
        // are the same shape: the keys are the members.
        if (typeof(System.Dynamic.IDynamicMetaObjectProvider).IsAssignableFrom(type)) return false;
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type)) return false;

        return !type.IsInterface && (type.IsValueType || type.IsArray || type.IsSealed);
    }

    /// <summary>
    /// Checks a call against a method declared on a user class or struct — <c>TS-P2-79</c>.
    /// </summary>
    /// <remarks>
    /// An argument is reported only when *every* arity-matching overload rejects it, which is the
    /// rule the CLR path already applies: one overload accepting the call makes it good.
    /// </remarks>
    /// <summary>
    /// Checks a constructor call against a user class's parameters — <c>TS-P2-79</c>.
    /// </summary>
    private static void CheckNewObject(BoundNewObject newObject, CheckContext ctx)
    {
        if (!UserTypeMembers.IsReadable(newObject.Type)) return;
        if (newObject.Arguments.Any(a => a.IsSplat || a.Name is not null)) return;

        var constructors = UserTypeMembers.GetConstructors(newObject.Type);

        // A class with no declared constructor takes its fields positionally at runtime, which
        // this does not model — so it is left alone rather than guessed at.
        if (constructors.Count == 0) return;

        CheckAgainstParameterLists(
            constructors,
            newObject.Arguments,
            $"Constructor of '{newObject.Type.DisplayName}'",
            newObject.Span,
            ctx);
    }

    /// <summary>
    /// Checks a write to a property of a user class against its annotation — <c>TS-P2-79</c>.
    /// </summary>
    /// <remarks>
    /// Only a plain <c>=</c> is checked. A compound operator (<c>+=</c>, <c>??=</c>) combines the
    /// existing value with the new one, so the assigned type is not the operand's type and
    /// reporting on it would be wrong.
    /// </remarks>
    private static void CheckUserMemberAssignment(BoundMemberAssignment assignment, CheckContext ctx)
    {
        if (!string.Equals(assignment.Operator, "=", StringComparison.Ordinal)) return;
        if (assignment.Target is not BoundMemberAccess access) return;

        var targetType = access.Target.Type;
        if (!UserTypeMembers.IsReadable(targetType)) return;

        var segments = access.MemberPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1) return;

        if (!UserTypeMembers.TryGetProperty(targetType, segments[0], out var property)) return;

        var declared = ResolverFor(ctx).Resolve(property.TypeName);
        if (declared.IsDynamic) return;

        var actual = TypeInferrer.InferPipelineValue(assignment.Value);
        if (actual.IsDynamic || IsAssignable(actual, declared, out _)) return;

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.type.mismatch",
            Title: $"Cannot assign value of type '{actual.DisplayName}' to property " +
                   $"'{property.Name}' of type '{declared.DisplayName}'.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: assignment.Span,
            Severity: ToshDiagnosticSeverity.Warning,
            Category: ToshDiagnosticCategory.Type,
            Lifecycle: ToshDiagnosticLifecycle.Preview));
    }

    private static void CheckUserMethodCall(BoundMethodCall call, BoundType targetType, CheckContext ctx)
    {
        var overloads = UserTypeMembers.GetMethods(targetType, call.MethodName);

        if (overloads.Count == 0)
        {
            // A base class, trait or partial half can carry the member, and none of those are
            // reachable from here — so absence is only reported when the declaration is complete.
            if (UserTypeMembers.MayHaveUnseenMembers(targetType)) return;
            if (ctx.ExtensionMethodNames.Contains(call.MethodName)) return;

            // A *property* of that name can hold a callable, and calling one is legal —
            // `TS-P2-93` taught the `$this.Handler(…)` path that rule, and this one was not
            // covered. `TS-P2-118`: the runtime was right and the checker was wrong, which
            // is the worst pairing, because the code works and the warning is noise. Noise
            // is what teaches people to stop reading warnings.
            //
            // Asked through the same `UserTypeMembers` lookup the member-access check uses,
            // rather than restating what "callable property" means. Whether the property's
            // *value* is callable cannot be known here — the checker holds annotation names,
            // not declarations, as `CheckUserMemberAccess` records two methods below — so a
            // declared property suppresses the warning outright. That trades a warning
            // nobody was getting for one nobody wanted.
            if (UserTypeMembers.TryGetProperty(targetType, call.MethodName, out _)) return;

            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.member_not_found",
                Title: $"Method '{call.MethodName}' was not found on type '{targetType.DisplayName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: call.Span,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
            return;
        }

        var parameterLists = overloads.Select(o => o.Method.Parameters).ToArray();
        CheckAgainstParameterLists(
            parameterLists,
            call.Arguments,
            $"Method '{call.MethodName}' on '{targetType.DisplayName}'",
            call.Span,
            ctx);
    }

    /// <summary>
    /// Checks a read of a member declared on a user class or struct — <c>TS-P2-79</c>.
    /// </summary>
    private static void CheckUserMemberAccess(BoundMemberAccess access, BoundType targetType, CheckContext ctx)
    {
        var segments = access.MemberPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;

        // Only the first segment is checkable: the member's own type is an annotation *name*, and
        // resolving it back to a declaration needs the registry the checker does not hold.
        var segment = segments[0];

        if (UserTypeMembers.Declares(targetType, segment)) return;
        if (UserTypeMembers.MayHaveUnseenMembers(targetType)) return;

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.type.member_not_found",
            Title: $"Member '{segment}' was not found on type '{targetType.DisplayName}'.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: access.Span,
            Severity: ToshDiagnosticSeverity.Warning,
            Category: ToshDiagnosticCategory.Type,
            Lifecycle: ToshDiagnosticLifecycle.Preview));
    }

    /// <summary>
    /// Reports an argument no overload accepts — <c>TS-P2-79</c>.
    /// </summary>
    /// <remarks>
    /// Shared by method and constructor calls, which differ only in what they are called. A list
    /// carrying an optional or rest parameter is skipped rather than guessed at, and an
    /// unannotated parameter accepts anything, so silence is the answer wherever the declaration
    /// did not commit to a type.
    /// </remarks>
    private static void CheckAgainstParameterLists(
        IReadOnlyList<IReadOnlyList<FunctionParameterSyntax>> parameterLists,
        IReadOnlyList<BoundArgument> arguments,
        string calleeLabel,
        TextSpan span,
        CheckContext ctx)
    {
        var arityMatch = parameterLists
            .Where(ps => ps.Count == arguments.Count && !ps.Any(p => p.IsOptional || p.IsRest))
            .ToArray();

        if (arityMatch.Length == 0)
        {
            var fixedArities = parameterLists
                .Where(ps => !ps.Any(p => p.IsOptional || p.IsRest))
                .ToArray();

            if (fixedArities.Length == 0 || fixedArities.Length != parameterLists.Count) return;

            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.arity",
                Title: $"{calleeLabel} does not accept {arguments.Count} argument(s).",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: span,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
            return;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var actual = arguments[index].Value.Type;
            if (actual.IsDynamic) continue;

            string? declaredName = null;
            var rejectedByAll = true;

            foreach (var parameters in arityMatch)
            {
                var declared = ResolverFor(ctx).Resolve(parameters[index].TypeName);

                if (declared.IsDynamic || IsAssignable(actual, declared, out _))
                {
                    rejectedByAll = false;
                    break;
                }

                declaredName ??= declared.DisplayName;
            }

            if (!rejectedByAll || declaredName is null) continue;

            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.mismatch",
                Title: $"Cannot pass a value of type '{actual.DisplayName}' as argument " +
                       $"{index + 1} of {calleeLabel}, which expects '{declaredName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: span,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
            return;
        }
    }

    private static void CheckMemberAccess(BoundMemberAccess access, CheckContext ctx)
    {
        var targetType = access.Target.Type;

        // `TS-P2-79`. Same reason as the method-call path: the CLR type is null for a user class.
        if (UserTypeMembers.IsReadable(targetType))
        {
            CheckUserMemberAccess(access, targetType, ctx);
            return;
        }

        if (!targetType.IsConcrete || targetType.ClrType is null) return;

        // MemberPath may be dotted; validate segment-by-segment.
        var clr = targetType.ClrType;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        foreach (var segment in access.MemberPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!MemberChecksAreSound(clr)) return;

            var member = clr.GetMember(segment, flags).FirstOrDefault();
            if (member is null)
            {
                ctx.Diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.type.member_not_found",
                    Title: $"Member '{segment}' was not found on type '{clr.Name}'.",
                    SourceName: ctx.SourceName,
                    SourceText: ctx.SourceText,
                    Span: access.Span,
                    Severity: ToshDiagnosticSeverity.Warning,
                    Category: ToshDiagnosticCategory.Type,
                    Lifecycle: ToshDiagnosticLifecycle.Preview));
                return;
            }

            clr = member switch
            {
                PropertyInfo pi => pi.PropertyType,
                FieldInfo fi => fi.FieldType,
                MethodInfo mi => mi.ReturnType,
                _ => clr,
            };
        }
    }

    private static void CheckMethodCall(BoundMethodCall call, CheckContext ctx)
    {
        var targetType = call.Target.Type;

        if (call.Arguments.Any(a => a.IsSplat || a.Name is not null)) return;

        // `TS-P2-79`. A ToastScript class has no CLR type until it executes, so every rule below
        // used to bail on its first line and a method call against a user class was unchecked.
        // The declaration carries the annotations; this reads them.
        if (UserTypeMembers.IsReadable(targetType))
        {
            CheckUserMethodCall(call, targetType, ctx);
            return;
        }

        if (!targetType.IsConcrete || targetType.ClrType is null) return;

        // Same soundness rule as member access: an interface or open class cannot say what the
        // runtime type will offer.
        if (!MemberChecksAreSound(targetType.ClrType)) return;

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var overloads = targetType.ClrType
            .GetMethods(flags)
            .Where(m => string.Equals(m.Name, call.MethodName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (overloads.Length == 0)
        {
            if (ctx.ExtensionMethodNames.Contains(call.MethodName)) return;

            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.member_not_found",
                Title: $"Method '{call.MethodName}' was not found on type '{targetType.ClrType.Name}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: call.Span,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
            return;
        }

        var positional = call.Arguments;
        var arityMatch = overloads.Where(o => o.GetParameters().Length == positional.Count).ToArray();
        if (arityMatch.Length == 0)
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.arity",
                Title: $"Method '{call.MethodName}' on '{targetType.ClrType.Name}' does not accept {positional.Count} argument(s).",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: call.Span,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
            return;
        }

        foreach (var method in arityMatch)
        {
            var ps = method.GetParameters();
            var ok = true;
            for (var i = 0; i < ps.Length; i++)
            {
                var actual = positional[i].Value.Type;
                var expected = BoundType.FromClr(ps[i].ParameterType);
                if (!IsAssignable(actual, expected, out _)) { ok = false; break; }
            }
            if (ok) return;
        }

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.type.mismatch",
            Title: $"No overload of '{call.MethodName}' on '{targetType.ClrType.Name}' matches the provided argument types.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: call.Span,
            Severity: ToshDiagnosticSeverity.Warning,
            Category: ToshDiagnosticCategory.Type,
            Lifecycle: ToshDiagnosticLifecycle.Preview));
    }

    /// <summary>
    /// The key type of <paramref name="type"/> when it is a dictionary, taking
    /// the non-generic <see cref="System.Collections.IDictionary"/> to be keyed
    /// by <see cref="object"/>.
    /// </summary>
    private static bool TryGetDictionaryKeyType(Type type, out Type keyType)
    {
        foreach (var candidate in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                keyType = candidate.GetGenericArguments()[0];
                return true;
            }
        }

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            keyType = typeof(object);
            return true;
        }

        keyType = typeof(object);
        return false;
    }

    private static void CheckIndexAccess(BoundIndexAccess access, CheckContext ctx)
    {
        var indexType = access.Index.Type;
        if (indexType.IsDynamic) return;

        // Skip the check entirely when the target is dynamic or implements
        // IShellRecordObject (record-object protocol — supports string keys
        // even when LookupKind didn't make that explicit, e.g. $foo["bar"]
        // on an unknown-shape config object).
        var targetType = access.Target.Type;
        if (targetType.IsDynamic) return;
        if (targetType.ClrType is { } tc && typeof(IShellRecordObject).IsAssignableFrom(tc)) return;

        // A dictionary is indexed by its key type, not by position. Without this
        // the check assumed an integer index and warned on every `$d["k"]`,
        // which is the ordinary way to read a dict. An `object` key accepts
        // anything, so the check only bites when the key type is specific.
        if (targetType.ClrType is { } dictType &&
            TryGetDictionaryKeyType(dictType, out var keyType))
        {
            if (keyType == typeof(object))
            {
                return;
            }

            if (!IsAssignable(indexType, BoundType.FromClr(keyType), out var keyReason))
            {
                ctx.Diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.type.index",
                    Title: $"Dictionary is keyed by '{BoundType.FromClr(keyType).DisplayName}' but received '{indexType.DisplayName}'.",
                    SourceName: ctx.SourceName,
                    SourceText: ctx.SourceText,
                    Span: access.Span,
                    Help: keyReason,
                    Severity: ToshDiagnosticSeverity.Warning,
                    Category: ToshDiagnosticCategory.Type,
                    Lifecycle: ToshDiagnosticLifecycle.Preview));
            }

            return;
        }

        var expectsString = access.LookupKind is IndexLookupKind.ByKey;
        var expected = expectsString ? BoundType.FromClr(typeof(string)) : BoundType.FromClr(typeof(int));
        if (!IsAssignable(indexType, expected, out var reason))
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.index",
                Title: $"Index access expects '{expected.DisplayName}' index but received '{indexType.DisplayName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: access.Span,
                Help: reason,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
        }
    }

    private static void CheckBinaryOperator(BoundBinaryOperator binary, CheckContext ctx)
    {
        var left = binary.Left.Type;
        var right = binary.Right.Type;
        if (left.IsDynamic || right.IsDynamic) return;

        bool isNumeric(BoundType t) => t.ClrType is { } c && NumericRank(c) > 0;
        bool isString(BoundType t) => t.ClrType == typeof(string);

        // If either operand isn't a built-in scalar, skip the check —
        // user-defined classes (and unresolved CLR types) may carry
        // operator overloads that the checker can't see.
        bool isPrimitiveScalar(BoundType t)
        {
            var c = t.ClrType;
            if (c is null) return false;
            return c == typeof(bool) || c == typeof(string) || c == typeof(char)
                || c == typeof(DateTime) || NumericRank(c) > 0;
        }
        if (!isPrimitiveScalar(left) || !isPrimitiveScalar(right)) return;

        var op = binary.Operator;
        var ok = op switch
        {
            "+" => (isNumeric(left) && isNumeric(right)) || (isString(left) || isString(right)),
            "-" or "*" or "/" or "%" or "**" => isNumeric(left) && isNumeric(right),
            "&&" or "||" or "and" or "or" => true,
            // TS-P1-14: mirror the runtime ordering rule exactly —
            // booleans are unordered and a string orders only against a
            // string. Everything else (numerics, chars, dates, and any
            // other IComparable pair) is left to the runtime, which
            // decides by convertibility. Requiring both operands to be
            // numeric here previously made `"a" < "b"` a compile error
            // even though it is valid and specified.
            "<" or "<=" or ">" or ">=" =>
                left.ClrType != typeof(bool)
                && right.ClrType != typeof(bool)
                && isString(left) == isString(right),
            "==" or "!=" => true,
            _ => true,
        };

        if (!ok)
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.operator",
                Title: $"Operator '{op}' is not compatible with operand types '{left.DisplayName}' and '{right.DisplayName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: binary.Span,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
        }
    }

    private static void CheckUnaryOperator(BoundUnaryOperator unary, CheckContext ctx)
    {
        var operand = unary.Operand.Type;
        if (operand.IsDynamic) return;

        var ok = unary.Operator switch
        {
            "-" or "+" => operand.ClrType is { } c &&
                (NumericRank(c) > 0 || typeof(Quantity).IsAssignableFrom(c)),
            "!" or "not" => true,
            _ => true,
        };

        if (!ok)
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.operator",
                Title: $"Unary operator '{unary.Operator}' is not compatible with operand type '{operand.DisplayName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: unary.Span,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
        }
    }

    private static string DescribeArity(int required, int max) =>
        max == int.MaxValue ? $"at least {required} argument(s)"
        : required == max ? $"{required} argument(s)"
        : $"between {required} and {max} argument(s)";

    // ── individual checks ─────────────────────────────────────

    /// <summary>
    /// Resolves member annotations, which arrive as names rather than as bound types
    /// (<c>TS-P2-22</c>).
    /// </summary>
    /// <remarks>
    /// Built without user types, the way <c>Lowerer</c>'s own probe is: a name this cannot
    /// resolve comes back as <see cref="BoundType.Dynamic"/> and is skipped, so the pass
    /// stays free of false positives on class and CLR names it cannot see from here. That
    /// bounds what this check covers, and is why the item's remaining positions are filed
    /// rather than half-done.
    /// </remarks>
    /// <summary>
    /// Resolves a member's declared type name, seeded with the unit's own declarations —
    /// `TOAST-0038`.
    /// </summary>
    /// <remarks>
    /// This used to be a single static resolver built with <c>userTypes: null</c>, which
    /// was harmless only while a name it could not resolve became <c>dynamic</c> and the
    /// check bowed out. `TOAST-0034` gave <see cref="TypeNameResolver"/> the platform index,
    /// and a resolver with no user types then answered a *user* type name with whatever CLR
    /// type happens to share it — so `prop Operand: Node = $operand` reported "Cannot assign
    /// value of type 'Node' to property 'Operand' of type 'Node'", the two `Node`s being
    /// different types with one name.
    ///
    /// Seeding it is the fix rather than removing the index: the resolver checks user types
    /// *before* the index, so a program's own declarations win as soon as it has them.
    /// </remarks>
    private static TypeNameResolver ResolverFor(CheckContext ctx) => ctx.MemberTypeResolver;

    /// <summary>
    /// A class or struct property's annotation is checked against its initializer, with
    /// the same rule and severity as a <c>var</c> declaration (<c>TS-P2-22</c>).
    /// </summary>
    /// <remarks>
    /// The members were already walked — the initializer *pipeline* was checked — but the
    /// annotation was never compared against it, so <c>prop X: int = "42"</c> reported
    /// nothing while <c>var x: int = "42"</c> reported `tosh.type.mismatch`. Silence, not
    /// disagreement: the runtime converts in both cases, so this was a hole in static
    /// coverage rather than a semantic split.
    /// </remarks>
    private static void CheckMemberAnnotation(BoundClassPropertyMember prop, CheckContext ctx)
    {
        if (prop.Initializer is null) return;

        var declared = ResolverFor(ctx).Resolve(prop.TypeName);
        if (declared.IsDynamic) return;

        var actual = TypeInferrer.InferPipelineValue(prop.Initializer);
        if (IsAssignable(actual, declared, out var reason)) return;

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.type.mismatch",
            Title: $"Cannot assign value of type '{actual.DisplayName}' to property '{prop.Name}' of type '{declared.DisplayName}'.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: prop.Span,
            Help: reason,
            Severity: ToshDiagnosticSeverity.Warning,
            Category: ToshDiagnosticCategory.Type,
            Lifecycle: ToshDiagnosticLifecycle.Preview));
    }

    private static void CheckVariableDeclaration(BoundVariableDeclaration decl, CheckContext ctx)
    {
        var declared = decl.Symbol.DeclaredType;
        if (decl.Value is null) return;
        var value = TypeInferrer.InferPipelineValue(decl.Value);
        if (!IsAssignable(value, declared, out var reason))
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.mismatch",
                Title: $"Cannot assign value of type '{value.DisplayName}' to variable '{decl.Symbol.Name}' of type '{declared.DisplayName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: decl.Span,
                Help: reason,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
        }
    }

    private static void CheckReturn(BoundReturnStatement ret, CheckContext ctx)
    {
        var expected = ctx.CurrentReturnType;
        if (expected is null) return;
        if (ret.Value is null)
        {
            // Bare `return;` only checks against void / dynamic.
            if (!expected.IsVoid && !expected.IsDynamic)
            {
                ctx.Diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.type.mismatch",
                    Title: $"Function declared '-> {expected.DisplayName}' returns no value.",
                    SourceName: ctx.SourceName,
                    SourceText: ctx.SourceText,
                    Span: ret.Span,
                    Severity: ToshDiagnosticSeverity.Warning,
                    Category: ToshDiagnosticCategory.Type,
                    Lifecycle: ToshDiagnosticLifecycle.Preview));
            }
            return;
        }
        var actual = TypeInferrer.InferPipelineValue(ret.Value);
        if (!IsAssignable(actual, expected, out var reason))
        {
            ctx.Diagnostics.Add(new ToshDiagnostic(
                Code: "tosh.type.mismatch",
                Title: $"Cannot return value of type '{actual.DisplayName}' from function declared '-> {expected.DisplayName}'.",
                SourceName: ctx.SourceName,
                SourceText: ctx.SourceText,
                Span: ret.Span,
                Help: reason,
                Severity: ToshDiagnosticSeverity.Warning,
                Category: ToshDiagnosticCategory.Type,
                Lifecycle: ToshDiagnosticLifecycle.Preview));
        }
    }

    // ── assignability ─────────────────────────────────────────

    /// <summary>
    /// Returns true when a value of <paramref name="from"/> can flow
    /// into a slot of <paramref name="to"/> without an explicit cast.
    /// Conservative: dynamic on either side returns true. Numeric
    /// widening (int→long, int→double, …) is allowed; narrowing is
    /// not. Everything else is exact-CLR-type equality.
    /// </summary>
    private static bool IsAssignable(BoundType from, BoundType to, out string? reason)
    {
        reason = null;
        if (to.IsDynamic || from.IsDynamic) return true;
        if (to.IsVoid && from.IsVoid) return true;

        // Type-alias transparency. A `RefinementType` is a named
        // wrapper around an underlying base type — both refinement-
        // bearing aliases (e.g. `type Positive = int where _ > 0`)
        // and plain aliases (e.g. `type Id = int`) reach this
        // checker via the `RefinementType` shape. For assignability
        // we unwrap the wrapper and compare against the base; the
        // refinement clauses themselves are validated dynamically
        // by the runtime/IL path on the actual value.
        if (from is RefinementType frt) return IsAssignable(frt.Base, to, out reason);
        if (to is RefinementType trt) return IsAssignable(from, trt.Base, out reason);

        // Generic alias instantiation: when the LHS is a constructed
        // generic whose template is itself an alias (e.g.
        // `MyList<T> = list<T>` used as `MyList<string>`), the
        // template carries the open base. Recurse against that base
        // — its element types may be `Dynamic` placeholders for the
        // alias's type parameters, which the IsDynamic short-circuit
        // happily accepts. This is intentionally lenient; precise
        // generic-alias substitution is a separate follow-up.
        if (to is GenericInstanceType toGiAlias && toGiAlias.Template is RefinementType toGiTpl)
        {
            return IsAssignable(from, toGiTpl.Base, out reason);
        }
        if (from is GenericInstanceType fromGiAlias && fromGiAlias.Template is RefinementType fromGiTpl)
        {
            return IsAssignable(fromGiTpl.Base, to, out reason);
        }

        // Exact match on the BoundType structure (handles list<int> ==
        // list<int>, user types, refinements, function types, etc.).
        if (from.Equals(to)) return true;

        // Element-wise recursion for the homogeneous container shapes.
        // The `Equals` short-circuit above already covers the
        // identical-element case; this branch additionally lets a
        // `Dynamic`-element flow into a slot with a concrete element
        // type, and pairs structurally so nested shapes are checked
        // recursively rather than via the loose CLR-type fallback
        // (which conflates `List<T>` with `IList`).
        if (from is ListType fromList && to is ListType toList)
        {
            return IsAssignable(fromList.Element, toList.Element, out reason);
        }
        if (from is ArrayType fromArr && to is ArrayType toArr)
        {
            return IsAssignable(fromArr.Element, toArr.Element, out reason);
        }
        if (from is SetType fromSet && to is SetType toSet)
        {
            return IsAssignable(fromSet.Element, toSet.Element, out reason);
        }
        if (from is DictType fromDict && to is DictType toDict)
        {
            return IsAssignable(fromDict.Key, toDict.Key, out reason)
                && IsAssignable(fromDict.Value, toDict.Value, out reason);
        }

        // Loose-list-literal compatibility. Today the lowerer types
        // every list literal as the non-generic
        // `System.Collections.IList` regardless of element shape, so
        // a `list<int>` slot fed by `[1,2,3]` looks like
        // `IList -> List<int>` to the CLR-type fallback below \u2014 a
        // false negative. Accept any source whose CLR shape is the
        // raw `IList` (with no element type at the BoundType level)
        // when the destination is one of our structured list/array
        // shapes; the runtime conversion handles the actual element
        // coercion.
        if (from.ClrType == typeof(System.Collections.IList)
            && from is not ListType && from is not ArrayType
            && (to is ListType or ArrayType
                // `TS-P2-84`. An annotation written `-> object[]` resolves to a *concrete* CLR
                // array rather than to `ArrayType`, so the structured test above missed it and
                // `func f() -> object[] { return [1,2] }` was reported as returning the wrong
                // type — while running correctly, since the runtime converts. The same loose
                // list literal, the same conversion, one shape further out.
                || (to.ClrType is { } toClr
                    && (toClr.IsArray || typeof(System.Collections.IList).IsAssignableFrom(toClr)))))
        {
            return true;
        }
        if (from.ClrType == typeof(System.Collections.IDictionary)
            && from is not DictType
            && to is DictType)
        {
            return true;
        }

        // stream<T> models the polymorphic pipeline materialization
        // rule (single-element → T, multi-element → T[] / list<T>).
        // Allow stream<T> on the source side to flow into a slot
        // typed as T, list<T>, T[], or another stream<T>. Recurses
        // on the element type so nested structural matches work.
        if (from is StreamType st)
        {
            if (to is StreamType destStream && IsAssignable(st.Element, destStream.Element, out _)) return true;
            if (to is ListType destList && IsAssignable(st.Element, destList.Element, out _)) return true;
            if (to is ArrayType destArray && IsAssignable(st.Element, destArray.Element, out _)) return true;
            if (IsAssignable(st.Element, to, out _)) return true;
        }

        // Concrete CLR types: allow exact match plus numeric widening.
        if (from.ClrType is { } fc && to.ClrType is { } tc)
        {
            if (fc == tc) return true;
            if (IsNumericWidening(fc, tc)) return true;
            // Quantity annotations intentionally parse shell/argv strings at
            // runtime. Treat that documented conversion as assignable so the
            // preview checker does not warn on `in-feet "2mi"` even though both
            // interpreted and compiled boundaries accept it.
            if (fc == typeof(string) && typeof(Quantity).IsAssignableFrom(tc)) return true;
            // A nullable<T> slot accepts T.
            if (to is NullableType nt && nt.Inner.ClrType == fc) return true;
            if (tc.IsAssignableFrom(fc)) return true;
            reason = $"no implicit conversion from '{fc.Name}' to '{tc.Name}'.";
            return false;
        }

        // Generic-instance ⇄ bare-template equivalence. A generic class
        // declared `class Point2D<T>` may be referenced inside its own
        // body either as the bare name `Point2D` (when annotating a
        // return type) or as a constructed `Point2D<T>`. Treat
        // `GenericInstanceType { Template = X }` and the bare user
        // template `X` as compatible whenever the templates match;
        // recurse on type arguments pointwise so nested shapes still
        // get checked. Type parameters and dynamic args are accepted
        // (handled by the IsDynamic short-circuit at the top).
        if (from is GenericInstanceType fromGi && SameUserTemplate(fromGi.Template, to))
        {
            return true;
        }
        if (to is GenericInstanceType toGi && SameUserTemplate(toGi.Template, from))
        {
            return true;
        }
        if (from is GenericInstanceType fgi && to is GenericInstanceType tgi
            && SameUserTemplate(fgi.Template, tgi.Template)
            && fgi.TypeArguments.Count == tgi.TypeArguments.Count)
        {
            // Variance dispatch. For interface templates, consult
            // the declared `out`/`in` annotations on each type
            // parameter and apply the matching directional check.
            // All other templates (classes, records, structs, …)
            // remain invariant — matching C# semantics, where only
            // interface (and delegate) parameters can declare
            // variance.
            var variances = GetTemplateVariances(fgi.Template, fgi.TypeArguments.Count);
            for (var i = 0; i < fgi.TypeArguments.Count; i++)
            {
                var fromArg = fgi.TypeArguments[i];
                var toArg = tgi.TypeArguments[i];
                var ok = variances[i] switch
                {
                    Tosh.Language.Parsing.TypeParameterVariance.Covariant
                        => IsAssignable(fromArg, toArg, out _),
                    Tosh.Language.Parsing.TypeParameterVariance.Contravariant
                        => IsAssignable(toArg, fromArg, out _),
                    // Invariant: bidirectional assignability — accepts
                    // pure equality plus alias/refinement unwraps and
                    // dynamic placeholders, but rejects asymmetric
                    // narrowing/widening that pure covariance would
                    // accept.
                    _ => IsAssignable(fromArg, toArg, out _) && IsAssignable(toArg, fromArg, out _),
                };
                if (!ok) goto notAssignable;
            }
            return true;
        notAssignable:;
        }

        // A subclass is assignable to its base. Without this, an AST-shaped
        // hierarchy could not be typed at all: `return new LetNode(…)` from a
        // function declared `-> Node` was rejected, and so was passing one to a
        // `Node` parameter — `TS-P2-107`, and the runtime half of it,
        // `TS-P2-109`.
        if (from is UserClassType fromClass && to is UserClassType toClass &&
            DerivesFrom(fromClass.Name, toClass.Name))
        {
            return true;
        }

        // An interface or trait annotation accepts any class that fulfills it —
        // `func render(d: Drawable)` is the shape of every polymorphic API, and
        // rejecting it forced such signatures to go unannotated (`TS-P2-99`).
        if (from is UserClassType contractClass &&
            to is UserInterfaceType or UserTraitType &&
            SatisfiesContract(contractClass.Name, to.DisplayName))
        {
            return true;
        }

        reason = "shapes differ.";
        return false;
    }

    /// <summary>
    /// Returns the per-type-parameter variance list for a generic
    /// template. Falls back to all-invariant when the template
    /// doesn't carry variance metadata (i.e. anything other than a
    /// user interface). The returned list is exactly
    /// <paramref name="arity"/> elements long for safe indexing.
    /// </summary>
    private static IReadOnlyList<Tosh.Language.Parsing.TypeParameterVariance> GetTemplateVariances(
        BoundType template, int arity)
    {
        if (template is UserInterfaceType iface
            && iface.Definition is Tosh.Language.Parsing.InterfaceDefinitionStatementSyntax def
            && def.TypeParameterVariances is { Count: > 0 } declared
            && declared.Count == arity)
        {
            return declared;
        }
        return Enumerable.Repeat(Tosh.Language.Parsing.TypeParameterVariance.Invariant, arity).ToList();
    }

    /// <summary>
    /// Compares two user-type bound nodes by template identity. Used
    /// by the generic-instance ⇄ bare-template assignability rule so
    /// that <c>Point2D&lt;T&gt;</c> and <c>Point2D</c> match without
    /// the latter being expanded into an explicit instance form.
    /// </summary>
    private static bool SameUserTemplate(BoundType a, BoundType b)
    {
        return (a, b) switch
        {
            (UserClassType ax, UserClassType bx) => string.Equals(ax.Name, bx.Name, StringComparison.Ordinal),
            (UserRecordType ax, UserRecordType bx) => string.Equals(ax.Name, bx.Name, StringComparison.Ordinal),
            (UserStructType ax, UserStructType bx) => string.Equals(ax.Name, bx.Name, StringComparison.Ordinal),
            (UserInterfaceType ax, UserInterfaceType bx) => string.Equals(ax.Name, bx.Name, StringComparison.Ordinal),
            (UserUnionType ax, UserUnionType bx) => string.Equals(ax.Name, bx.Name, StringComparison.Ordinal),
            (UserTraitType ax, UserTraitType bx) => string.Equals(ax.Name, bx.Name, StringComparison.Ordinal),
            (UserEnumType ax, UserEnumType bx) => string.Equals(ax.Name, bx.Name, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsNumericWidening(Type from, Type to)
    {
        var fr = NumericRank(from);
        var tr = NumericRank(to);
        return fr > 0 && tr > 0 && fr <= tr;
    }

    private static int NumericRank(Type t) => t switch
    {
        _ when t == typeof(byte) => 1,
        _ when t == typeof(sbyte) => 1,
        _ when t == typeof(short) => 2,
        _ when t == typeof(ushort) => 2,
        _ when t == typeof(int) => 3,
        _ when t == typeof(uint) => 3,
        _ when t == typeof(long) => 4,
        _ when t == typeof(ulong) => 4,
        _ when t == typeof(float) => 5,
        _ when t == typeof(double) => 6,
        _ when t == typeof(decimal) => 7,
        _ => 0,
    };
}

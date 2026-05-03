using Tosh.Language.Binding.BoundNodes;
using Tosh.Runtime;

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
        // Pre-pass: harvest user-function signatures so call-site
        // arity/argument checks can fire without forward-reference
        // pain. Function bodies are walked inside this same pass —
        // there is no separate resolution phase.
        CollectUserFunctions(unit.Root, ctx.UserFunctions);
        Walk(unit.Root, ctx);
        return ctx.Diagnostics;
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
    ///     could not pin down a concrete type. Suppressible via
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
                SourceName: unit.ParseResult?.SourceName,
                SourceText: unit.ParseResult?.SourceText,
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
                    SourceName: unit.ParseResult?.SourceName,
                    SourceText: unit.ParseResult?.SourceText,
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
        // Already concretely typed -> nothing to flag.
        if (!decl.Symbol.DeclaredType.IsDynamic) return;
        diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.compile.implicit_dynamic",
            Title: $"Variable '{decl.Symbol.Name}' has no type annotation and the inferrer could not pin down a concrete type.",
            SourceName: unit.ParseResult?.SourceName,
            SourceText: unit.ParseResult?.SourceText,
            Span: decl.Span,
            Help: "annotate the variable (e.g. `var " + decl.Symbol.Name + ": int = ...`) or pass `--compile-allow-dynamic` to allow implicit dynamic.",
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
        public Dictionary<string, BoundFunctionDefinition> UserFunctions { get; } =
            new(StringComparer.Ordinal);
        public BoundType? CurrentReturnType { get; set; }
        public string? SourceName => Unit.ParseResult?.SourceName;
        public string? SourceText => Unit.ParseResult?.SourceText;
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
                    ctx.CurrentReturnType = fn.ReturnType;
                    Walk(fn.Body, ctx);
                    ctx.CurrentReturnType = prev;
                }
                break;

            case BoundVariableDeclaration decl:
                CheckVariableDeclaration(decl, ctx);
                break;

            case BoundReturnStatement ret:
                CheckReturn(ret, ctx);
                break;

            case BoundIfStatement i:
                Walk(i.ThenBlock, ctx);
                if (i.ElseBlock is not null) Walk(i.ElseBlock, ctx);
                break;

            case BoundForStatement f:
                Walk(f.Body, ctx);
                break;

            case BoundWhileStatement w:
                Walk(w.Body, ctx);
                break;

            case BoundPipelineStatement ps:
                CheckPipeline(ps.Pipeline, ctx);
                break;

            case BoundVariableAssignment va:
                CheckPipeline(va.Value, ctx);
                break;
        }
    }

    private static void CheckPipeline(BoundPipeline pipeline, CheckContext ctx)
    {
        foreach (var stage in pipeline.Stages)
        {
            if (stage is BoundCommandCall call)
            {
                CheckCommandCall(call, ctx);
            }
        }
    }

    private static void CheckCommandCall(BoundCommandCall call, CheckContext ctx)
    {
        if (!ctx.UserFunctions.TryGetValue(call.Name, out var fn)) return;

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

    private static string DescribeArity(int required, int max) =>
        max == int.MaxValue ? $"at least {required} argument(s)"
        : required == max ? $"{required} argument(s)"
        : $"between {required} and {max} argument(s)";

    // ── individual checks ─────────────────────────────────────

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

        // Exact match on the BoundType structure (handles list<int> ==
        // list<int>, user types, refinements, function types, etc.).
        if (from.Equals(to)) return true;

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
            // A nullable<T> slot accepts T.
            if (to is NullableType nt && nt.Inner.ClrType == fc) return true;
            if (tc.IsAssignableFrom(fc)) return true;
            reason = $"no implicit conversion from '{fc.Name}' to '{tc.Name}'.";
            return false;
        }

        reason = "shapes differ.";
        return false;
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

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Binding;

/// <summary>
/// Phase 2 of the binder: variable-name scope analysis.
///
/// Walks the AST tracking a stack of lexical scopes and emits
/// <c>tosh.bind.unknown_variable</c> when a <c>$identifier</c>
/// reference looks like a typo for an in-scope name (Levenshtein
/// near-match, mirroring the command binder rule). References whose
/// names have no plausible near match in scope are left alone — the
/// runtime will resolve them, since they may be set by an outer
/// sourced file or environment.
/// </summary>
/// <remarks>
/// Scope-introducing constructs:
///   var X = …                      adds X to current scope
///   var [a, b] = …                 adds a and b to current scope
///   var {a, b} = …                 adds a and b
///   func f(p1, p2) { … }           opens a scope with p1, p2
///   for $i in … { … }              opens a scope with i for the body
///   try { … } catch $e { … }       opens a catch scope with e
///   |x| { … } / lambda             opens a scope with parameters
///   class C(p1) { prop X = … … }   opens a scope with primary-ctor params
///                                    and property names; class methods/ctors
///                                    extend it with their own parameters
///   module M { … }                 visited as a fresh outermost scope
///
/// Always-allowed roots (never flagged): env, tosh, this, super, args, _.
///
/// Member access is checked at the root only. <c>$person.namee</c>
/// validates <c>person</c>; the member tail is left to runtime since
/// the binder has no record-shape information.
///
/// String interpolation: <c>InterpolatedStringExpressionPart.Expression</c>
/// is a tosh source fragment — running the parser on each one is too
/// heavy for the binder. Instead we extract <c>$name</c> tokens with a
/// regex and check the names against the active scope. Diagnostic spans
/// point at the precise <c>$name</c> occurrence inside the source string,
/// using <see cref="InterpolatedStringExpressionPart.ExpressionSpan"/> as
/// the anchor.
/// </remarks>
public static class VariableBinder
{
    private const int ShortNameLevenshteinThreshold = 1;
    private const int LongNameLevenshteinThreshold = 2;
    private const int ShortNameMaxLength = 4;
    private const int MaxSuggestions = 3;

    private static readonly HashSet<string> AlwaysAllowed =
        new(StringComparer.Ordinal) { "env", "tosh", "this", "super", "args", "_" };

    private static readonly Regex InterpolationVariableRegex = new(
        @"(?<!\\)\$([a-zA-Z_][a-zA-Z_0-9]*)",
        RegexOptions.Compiled);

    public static IReadOnlyList<ToshDiagnostic> Bind(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var ctx = new Context(
            parseResult.SourceName,
            parseResult.SourceText,
            new List<HashSet<string>> { new(StringComparer.Ordinal) },
            new List<ToshDiagnostic>(),
            CollectPatternShapes(parseResult.Statement),
            CollectVariantUnions(parseResult.Statement));

        VisitStatement(parseResult.Statement, ctx);
        return ctx.Diagnostics;
    }

    // ──────────────────────────────────────────────────────────────────
    // Scope helpers
    // ──────────────────────────────────────────────────────────────────

    private static IDisposable Push(Context ctx, IEnumerable<string>? initial = null)
    {
        var scope = new HashSet<string>(StringComparer.Ordinal);
        if (initial is not null)
        {
            foreach (var name in initial) scope.Add(name);
        }
        ctx.Scopes.Add(scope);
        return new ScopeFrame(ctx);
    }

    private static void Declare(Context ctx, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        ctx.Scopes[^1].Add(name);
    }

    private static bool IsKnown(Context ctx, string name)
    {
        if (AlwaysAllowed.Contains(name)) return true;
        for (var i = ctx.Scopes.Count - 1; i >= 0; i--)
        {
            if (ctx.Scopes[i].Contains(name)) return true;
        }
        return false;
    }

    private sealed class ScopeFrame(Context ctx) : IDisposable
    {
        public void Dispose() => ctx.Scopes.RemoveAt(ctx.Scopes.Count - 1);
    }

    // ──────────────────────────────────────────────────────────────────
    // Statement visitor
    // ──────────────────────────────────────────────────────────────────

    private static void VisitStatement(StatementSyntax statement, Context ctx)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) VisitStatement(child, ctx);
                break;

            case PipelineStatementSyntax p:
                VisitPipeline(p.Pipeline, ctx);
                break;

            case VariableDeclarationStatementSyntax v:
                if (v.Value is not null) VisitPipeline(v.Value, ctx);
                Declare(ctx, v.Name);
                break;

            // `arg name : T = default` and `flag name` declare script inputs, which are
            // ordinary variables everywhere below. The binder never declared them, which cost
            // nothing while it walked only command arguments: `$frames` in
            // `examples/mandelbrot.tosh` sat in `var tF = $frames`, an expression stage the
            // walk skipped. Widening that walk for `TOAST-0053` made the reference visible and
            // it was reported as undeclared — for a variable the script does declare.
            case ScriptInputStatementSyntax scriptInput:
                foreach (var parameter in scriptInput.Parameters)
                {
                    if (parameter.DefaultValue is not null) { VisitPipeline(parameter.DefaultValue, ctx); }
                    Declare(ctx, parameter.Name);
                }

                break;

            case DestructuringDeclarationStatementSyntax d:
                VisitPipeline(d.Value, ctx);
                switch (d.Pattern)
                {
                    case ArrayDestructuringPatternSyntax arr:
                        foreach (var n in arr.Names) Declare(ctx, n);
                        break;
                    case RecordDestructuringPatternSyntax rec:
                        foreach (var n in rec.Names) Declare(ctx, n);
                        break;
                }
                break;

            case AllocStatementSyntax a:
                VisitPipeline(a.Value, ctx);
                break;

            case VariableAssignmentStatementSyntax va:
                // The LHS name must already be in scope for the assignment to mean
                // anything at runtime. Check it the same way as a reference, but
                // with the declared span.
                CheckIdentifier(va.Name, va.Span, ctx);
                VisitPipeline(va.Value, ctx);
                break;

            case MemberAssignmentStatementSyntax ma:
                VisitArgument(ma.Target, ctx);
                VisitPipeline(ma.Value, ctx);
                break;

            case ReturnStatementSyntax r when r.Value is not null:
                VisitPipeline(r.Value, ctx);
                break;
            case YieldStatementSyntax y when y.Value is not null:
                VisitPipeline(y.Value, ctx);
                break;
            case ThrowStatementSyntax t when t.Value is not null:
                VisitPipeline(t.Value, ctx);
                break;

            case FunctionDefinitionStatementSyntax func:
                {
                    using var _ = Push(ctx, func.Parameters.Select(p => p.Name));
                    foreach (var p in func.Parameters)
                    {
                        if (p.DefaultValue is not null) VisitPipeline(p.DefaultValue, ctx);
                    }
                    VisitBlock(func.Body, ctx, openScope: false);
                    break;
                }

            case RuneDefinitionStatementSyntax rune:
                {
                    using var _ = Push(ctx);
                    VisitBlock(rune.Body, ctx, openScope: false);
                    break;
                }

            case ClassDefinitionStatementSyntax cls:
                {
                    // Primary-constructor parameters and instance properties are
                    // visible inside class methods/constructors via $this.X — but
                    // since we only check the root, $this is always allowed. The
                    // class scope still helps for static prop initializers and
                    // method bodies that reference primary-ctor params directly.
                    using var classScope = Push(ctx, cls.PrimaryConstructorParameters.Select(p => p.Name));
                    foreach (var p in cls.PrimaryConstructorParameters)
                    {
                        if (p.DefaultValue is not null) VisitPipeline(p.DefaultValue, ctx);
                    }
                    foreach (var ba in cls.BaseConstructorArgs ?? Array.Empty<PipelineSyntax>())
                    {
                        VisitPipeline(ba, ctx);
                    }
                    foreach (var member in cls.Members)
                    {
                        switch (member)
                        {
                            case ClassPropertyMemberSyntax prop:
                                if (prop.Initializer is not null) VisitPipeline(prop.Initializer, ctx);
                                if (prop.GetterBody is not null) VisitBlock(prop.GetterBody, ctx);
                                if (prop.SetterBody is not null) VisitBlock(prop.SetterBody, ctx);
                                Declare(ctx, prop.Name);
                                break;
                            case ClassMethodMemberSyntax method:
                                {
                                    using var _ = Push(ctx, method.Method.Parameters.Select(p => p.Name));
                                    foreach (var p in method.Method.Parameters)
                                    {
                                        if (p.DefaultValue is not null) VisitPipeline(p.DefaultValue, ctx);
                                    }
                                    VisitBlock(method.Method.Body, ctx, openScope: false);
                                    break;
                                }
                            case ClassConstructorMemberSyntax ctor:
                                {
                                    using var _ = Push(ctx, ctor.Parameters.Select(p => p.Name));
                                    foreach (var p in ctor.Parameters)
                                    {
                                        if (p.DefaultValue is not null) VisitPipeline(p.DefaultValue, ctx);
                                    }
                                    VisitBlock(ctor.Body, ctx, openScope: false);
                                    break;
                                }
                        }
                    }
                    break;
                }

            case ModuleDefinitionStatementSyntax module:
                {
                    using var _ = Push(ctx);
                    VisitBlock(module.Body, ctx, openScope: false);
                    break;
                }

            case IfStatementSyntax @if:
                VisitArgument(@if.Condition, ctx);
                VisitBlock(@if.ThenBlock, ctx);
                if (@if.ElseBlock is not null) VisitBlock(@if.ElseBlock, ctx);
                break;

            case ForStatementSyntax @for:
                VisitPipeline(@for.Source, ctx);
                {
                    using var _ = Push(ctx, new[] { @for.VariableName });
                    VisitBlock(@for.Body, ctx, openScope: false);
                }
                break;

            case WhileStatementSyntax @while:
                VisitArgument(@while.Condition, ctx);
                VisitBlock(@while.Body, ctx);
                break;

            case UntilStatementSyntax @until:
                VisitArgument(@until.Condition, ctx);
                VisitBlock(@until.Body, ctx);
                break;

            case TryStatementSyntax @try:
                VisitBlock(@try.TryBlock, ctx);
                if (@try.CatchClause is not null)
                {
                    using var _ = Push(
                        ctx,
                        @try.CatchClause.VariableName is null ? null : new[] { @try.CatchClause.VariableName });
                    VisitBlock(@try.CatchClause.Body, ctx, openScope: false);
                }
                if (@try.FinallyBlock is not null) VisitBlock(@try.FinallyBlock, ctx);
                break;

            case DeferStatementSyntax @defer:
                VisitBlock(@defer.Body, ctx);
                break;

            case SwitchStatementSyntax @switch:
                VisitArgument(@switch.Value, ctx);
                foreach (var c in @switch.Cases)
                {
                    VisitArgument(c.MatchExpression, ctx);
                    if (c.Guard is not null) VisitArgument(c.Guard, ctx);
                    VisitBlock(c.Body, ctx);
                }
                if (@switch.DefaultBlock is not null) VisitBlock(@switch.DefaultBlock, ctx);
                break;

            case SubcommandStatementSyntax sub:
                VisitBlock(sub.Body, ctx);
                break;
        }
    }

    private static void VisitBlock(BlockSyntax block, Context ctx, bool openScope = true)
    {
        if (openScope)
        {
            using var _ = Push(ctx);
            foreach (var stmt in block.Statements) VisitStatement(stmt, ctx);
        }
        else
        {
            foreach (var stmt in block.Statements) VisitStatement(stmt, ctx);
        }
    }

    private static void VisitPipeline(PipelineSyntax pipeline, Context ctx)
    {
        foreach (var stage in pipeline.Stages)
        {
            switch (stage)
            {
                case CommandSyntax command:
                    foreach (var arg in command.Arguments) VisitArgument(arg, ctx);
                    break;

                // A stage that is an expression rather than a command was skipped entirely,
                // so nothing inside a bare `$x + 1`, a `| where { … }`, or any `match` was
                // ever bound-checked — the runtime reported those instead, from further away.
                case ExpressionPipelineStageSyntax expression:
                    VisitArgument(expression.Expression, ctx);
                    break;

                case PipeForwardStageSyntax forward:
                    foreach (var arg in forward.Command.Arguments) VisitArgument(arg, ctx);
                    break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Argument visitor — variable references live here.
    // ──────────────────────────────────────────────────────────────────

    private static void VisitArgument(ArgumentSyntax argument, Context ctx)
    {
        switch (argument)
        {
            case VariableReferenceArgumentSyntax v:
                CheckIdentifier(v.Name, v.Span, ctx);
                break;

            case MemberAccessArgumentSyntax m:
                // Only the root identifier is checked. The member tail has no
                // type information available to the binder.
                VisitArgument(m.Target, ctx);
                break;

            case IndexAccessArgumentSyntax idx:
                VisitArgument(idx.Target, ctx);
                VisitArgument(idx.Index, ctx);
                break;

            case MethodCallArgumentSyntax mc:
                VisitArgument(mc.Target, ctx);
                foreach (var a in mc.Arguments) VisitArgument(a, ctx);
                break;

            case CallableInvocationArgumentSyntax ci:
                VisitArgument(ci.Target, ctx);
                foreach (var a in ci.Arguments) VisitArgument(a, ctx);
                break;

            case BlockArgumentSyntax block:
                VisitBlock(block.Block, ctx);
                break;

            case NamedArgumentSyntax named:
                VisitArgument(named.Value, ctx);
                break;

            case SplatArgumentSyntax splat:
                VisitArgument(splat.Value, ctx);
                break;

            case ArrayLiteralArgumentSyntax arr:
                foreach (var item in arr.Items) VisitArgument(item, ctx);
                break;

            case SpreadElementArgumentSyntax spread:
                VisitArgument(spread.Value, ctx);
                break;

            case TupleLiteralArgumentSyntax tup:
                foreach (var item in tup.Items) VisitArgument(item, ctx);
                break;

            case SetLiteralArgumentSyntax set:
                foreach (var item in set.Items) VisitArgument(item, ctx);
                break;

            case RecordLiteralArgumentSyntax rec:
                foreach (var field in rec.Fields)
                {
                    switch (field)
                    {
                        case RecordFieldSyntax rf: VisitArgument(rf.Value, ctx); break;
                        case ComputedRecordFieldSyntax crf:
                            VisitArgument(crf.NameExpression, ctx);
                            VisitArgument(crf.Value, ctx);
                            break;
                        case SpreadRecordEntrySyntax sre: VisitArgument(sre.Value, ctx); break;
                    }
                }
                break;

            case DictLiteralArgumentSyntax dict:
                foreach (var entry in dict.Entries)
                {
                    VisitArgument(entry.Key, ctx);
                    VisitArgument(entry.Value, ctx);
                }
                break;

            case NewObjectArgumentSyntax newObj:
                foreach (var a in newObj.Arguments) VisitArgument(a, ctx);
                break;

            case AnonymousFunctionArgumentSyntax lam:
                {
                    using var _ = Push(ctx, lam.Parameters.Select(p => p.Name));
                    foreach (var p in lam.Parameters)
                    {
                        if (p.DefaultValue is not null) VisitPipeline(p.DefaultValue, ctx);
                    }
                    VisitBlock(lam.Body, ctx, openScope: false);
                    break;
                }

            case SubexpressionArgumentSyntax sub:
                VisitPipeline(sub.Pipeline, ctx);
                break;

            case CommandSubstitutionArgumentSyntax cs:
                VisitPipeline(cs.Pipeline, ctx);
                break;

            case InputProcessSubstitutionArgumentSyntax ip:
                VisitPipeline(ip.Pipeline, ctx);
                break;

            case OutputProcessSubstitutionArgumentSyntax op:
                VisitPipeline(op.Pipeline, ctx);
                break;

            case OperatorArgumentSyntax oper:
                VisitArgument(oper.Left, ctx);
                VisitArgument(oper.Right, ctx);
                break;

            case VariantPatternSyntax variantPattern:
                // `TOAST-0053`. A variant pattern's sub-patterns are ordinary arguments, so
                // one can hold a variable reference — `Item { kind: $expected }` compares a
                // field against a captured value. Not walking them would leave that reference
                // out of capture analysis, which is what `SyntaxTraversalExhaustivenessTests`
                // caught the moment the node was added.
                foreach (var element in variantPattern.Positional)
                {
                    VisitArgument(element, ctx);
                }

                foreach (var named in variantPattern.Named)
                {
                    VisitArgument(named.Pattern, ctx);
                }

                break;

            case OrPatternSyntax orPattern:
                // `TOAST-0053`. Every alternative is a pattern in its own right and may hold a
                // variable reference, so all of them are walked, not just the first.
                foreach (var alternative in orPattern.Alternatives)
                {
                    VisitArgument(alternative, ctx);
                }

                break;

            case BoundPatternSyntax boundPattern:
                VisitArgument(boundPattern.Pattern, ctx);
                break;

            case ListPatternSyntax listPattern:
                // `TOAST-0053`. Same reason as the variant pattern above: an element may be a
                // variable reference — `[$expected, second]` — so capture analysis has to see it.
                foreach (var element in listPattern.Before)
                {
                    VisitArgument(element, ctx);
                }

                foreach (var element in listPattern.After)
                {
                    VisitArgument(element, ctx);
                }

                break;

            case ComparisonPatternSyntax comparisonPattern:
                // A match arm's pattern operand can hold variable
                // references (`_ > $limit`), so it must be walked like any
                // other child (TS-P2-07).
                VisitArgument(comparisonPattern.Operand, ctx);
                break;

            case ChainedComparisonArgumentSyntax chain:
                foreach (var operand in chain.Operands)
                {
                    VisitArgument(operand, ctx);
                }
                break;

            case UnaryOperatorArgumentSyntax un:
                VisitArgument(un.Operand, ctx);
                break;

            case ConditionalArgumentSyntax cond:
                VisitArgument(cond.Condition, ctx);
                VisitArgument(cond.WhenTrue, ctx);
                VisitArgument(cond.WhenFalse, ctx);
                break;

            case IfExpressionArgumentSyntax ife:
                VisitArgument(ife.Condition, ctx);
                VisitBlock(ife.ThenBlock, ctx);
                VisitBlock(ife.ElseBlock, ctx);
                break;

            case MatchArgumentSyntax match:
                VisitArgument(match.Value, ctx);
                CheckMatchExhaustiveness(match, ctx);
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is not null) VisitArgument(arm.Pattern, ctx);

                    // `TOAST-0053`. A pattern's bindings are scoped to their arm, so the binder
                    // gets a scope per arm too — otherwise a name bound by one arm would look
                    // declared to the next, and the shadowing check below would fire on every
                    // arm after the first that reused a name.
                    using var armScope = Push(ctx);

                    if (arm.Pattern is not null)
                    {
                        CheckPatternShape(arm.Pattern, ctx);
                        DeclarePatternBindings(arm.Pattern, ctx);
                    }
                    if (arm.Guard is not null) VisitArgument(arm.Guard, ctx);
                    switch (arm.Body)
                    {
                        case MatchArmPipelineBodySyntax pb: VisitPipeline(pb.Pipeline, ctx); break;
                        case MatchArmBlockBodySyntax bb: VisitBlock(bb.Block, ctx); break;
                    }
                }
                break;

            case RangeArgumentSyntax range:
                VisitArgument(range.Start, ctx);
                if (range.Step is not null) VisitArgument(range.Step, ctx);
                if (range.End is not null) VisitArgument(range.End, ctx);
                break;

            case ThrowArgumentSyntax thr when thr.Value is not null:
                VisitArgument(thr.Value, ctx);
                break;

            case QuoteArgumentSyntax quote:
                // Quoted ASTs are introspected, not evaluated. Skip.
                break;

            case InterpolatedStringArgumentSyntax interp:
                CheckInterpolatedString(interp, ctx);
                break;

            case NameOfArgumentSyntax:
                // `nameof($x)` deliberately references a name without evaluating
                // it — don't flag.
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Identifier check
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every union variant and record declared in this source, with its fields in order —
    /// <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets a pattern's fields be checked where the pattern is *written* rather
    /// than when its arm is reached. A `union` is an ordinary statement evaluated at runtime,
    /// so the shapes are read from the syntax, the same way the binder already collects
    /// same-source function declarations.
    /// </para>
    /// <para>
    /// Only same-source declarations, deliberately. A type from a <c>require</c>d file is not
    /// here, and a pattern naming one is left alone rather than guessed at — a missed check
    /// costs a runtime diagnostic that still names the field, while a false one costs a
    /// program that will not run. Seeing another file's declarations is <c>TOAST-0052</c>.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectPatternShapes(
        StatementSyntax statement)
    {
        var shapes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        CollectPatternShapes(statement, shapes);
        return shapes;
    }

    private static void CollectPatternShapes(
        StatementSyntax statement,
        Dictionary<string, IReadOnlyList<string>> shapes)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) CollectPatternShapes(child, shapes);
                break;

            case UnionDefinitionStatementSyntax union:
                foreach (var variant in union.Variants)
                {
                    // A variant name declared twice in one source is the parser's problem, not
                    // this pass's — the first wins here rather than throwing.
                    shapes.TryAdd(
                        variant.Name,
                        variant.Fields.Select(field => field.Name).ToArray());
                }

                break;

            case RecordDefinitionStatementSyntax record:
                shapes.TryAdd(record.Name, record.Fields.Select(field => field.Name).ToArray());
                break;
        }
    }

    /// <summary>A closed union: its name, and every variant it declares, in order.</summary>
    internal sealed record UnionShape(string Name, IReadOnlyList<string> Variants);

    /// <summary>
    /// Every variant declared in this source, mapped to the union that declares it —
    /// <c>TOAST-0054</c>.
    /// </summary>
    /// <remarks>
    /// Keyed by *variant* rather than by union, because that is the direction the check reads
    /// it: an arm names a variant, and the union it belongs to is what says which other
    /// variants must also be covered. A variant name is enough to identify the union, which is
    /// why exhaustiveness needs no type for the matched value.
    /// </remarks>
    private static IReadOnlyDictionary<string, UnionShape> CollectVariantUnions(StatementSyntax statement)
    {
        var unions = new Dictionary<string, UnionShape>(StringComparer.Ordinal);
        CollectVariantUnions(statement, unions);
        return unions;
    }

    private static void CollectVariantUnions(
        StatementSyntax statement,
        Dictionary<string, UnionShape> unions)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) CollectVariantUnions(child, unions);
                break;

            case UnionDefinitionStatementSyntax union:
                var shape = new UnionShape(
                    union.Name,
                    union.Variants.Select(variant => variant.Name).ToArray());

                foreach (var variant in union.Variants)
                {
                    unions.TryAdd(variant.Name, shape);
                }

                break;
        }
    }

    /// <summary>
    /// Reports a <c>match</c> over a closed union that does not cover every variant —
    /// <c>TOAST-0054</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is not in catching the first mistake. It is in what happens when a variant is
    /// added to a union: either every <c>match</c> that must be updated is named now, or they
    /// are found later, on someone else's input.
    /// </para>
    /// <para>
    /// The union is identified from the *arms*, not from the matched value's type — a variant
    /// name belongs to exactly one union, so one arm is enough. That is what lets this run in
    /// the binder rather than waiting for the type checker to learn about unions.
    /// </para>
    /// <para>
    /// Three things make a match exempt, each deliberately. A <c>default</c> arm is the
    /// documented opt-out. An arm that is not a variant pattern — a literal, a comparison —
    /// means this is not a match over a union shape and is left alone, which is what keeps
    /// shell code free of new diagnostics. And a union declared in another source is invisible
    /// here, so nothing is claimed about it.
    /// </para>
    /// <para>
    /// A guarded arm does not cover its variant: it may not fire, so it cannot complete the
    /// match. `Add(l, r) if (…)` leaves `Add` uncovered, which is correct and occasionally
    /// surprising — the message says so rather than leaving the author to work it out.
    /// </para>
    /// </remarks>
    private static void CheckMatchExhaustiveness(MatchArgumentSyntax match, Context ctx)
    {
        UnionShape? union = null;
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var guardedOnly = new HashSet<string>(StringComparer.Ordinal);

        foreach (var arm in match.Arms)
        {
            // `default` is the opt-out, and it ends the question.
            if (arm.IsWildcard) { return; }
            if (arm.Pattern is null) { return; }

            foreach (var alternative in PatternAlternatives(arm.Pattern))
            {
                if (alternative is not VariantPatternSyntax variant)
                {
                    // Not a union-shaped match. Says nothing rather than guessing.
                    return;
                }

                if (!ctx.VariantUnions.TryGetValue(variant.VariantName, out var owner))
                {
                    // A variant this source cannot see; nothing can be claimed about the set.
                    return;
                }

                if (union is null) { union = owner; }
                else if (!ReferenceEquals(union, owner)) { return; }

                if (arm.Guard is null) { covered.Add(variant.VariantName); }
                else { guardedOnly.Add(variant.VariantName); }
            }
        }

        if (union is null) { return; }

        var missing = union.Variants.Where(name => !covered.Contains(name)).ToArray();
        if (missing.Length == 0) { return; }

        var guarded = missing.Where(guardedOnly.Contains).ToArray();
        var help = guarded.Length == 0
            ? missing.Length == 1
                ? "add an arm for it, or `default` if it shares an answer with the others."
                : "add an arm for each, or `default` if the rest genuinely share one."
            : $"an arm with a guard may not fire, so it does not complete the match — "
                + $"{string.Join(", ", guarded)} {(guarded.Length == 1 ? "is" : "are")} covered "
                + "only by a guarded arm. Add an unguarded arm, or `default`.";

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.bind.match_not_exhaustive",
            Title: $"This match over '{union.Name}' does not cover "
                 + $"{string.Join(", ", missing)}.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: match.Span,
            Label: $"'{union.Name}' declares: {string.Join(", ", union.Variants)}",
            Help: help));
    }

    /// <summary>The patterns an arm can match on — one, or an or-pattern's alternatives.</summary>
    private static IEnumerable<ArgumentSyntax> PatternAlternatives(ArgumentSyntax pattern)
    {
        switch (pattern)
        {
            case OrPatternSyntax alternatives:
                foreach (var alternative in alternatives.Alternatives)
                {
                    foreach (var inner in PatternAlternatives(alternative)) { yield return inner; }
                }

                break;

            case BoundPatternSyntax bound:
                foreach (var inner in PatternAlternatives(bound.Pattern)) { yield return inner; }
                break;

            default:
                yield return pattern;
                break;
        }
    }

    /// <summary>
    /// Checks a pattern's fields against the declaration it names — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// The same two mistakes the runtime reports, caught where they are written: a positional
    /// pattern asking for more fields than exist, and a named one naming a field that does
    /// not. The runtime keeps its checks, because a pattern may name a type this source cannot
    /// see; this only speaks when the declaration is right here.
    /// </remarks>
    private static void CheckPatternShape(ArgumentSyntax pattern, Context ctx)
    {
        switch (pattern)
        {
            case VariantPatternSyntax variant:
                if (ctx.PatternShapes.TryGetValue(variant.VariantName, out var fields))
                {
                    if (variant.Positional.Count > fields.Count)
                    {
                        ctx.Diagnostics.Add(new ToshDiagnostic(
                            Code: "tosh.bind.pattern_arity",
                            Title: $"Pattern for '{variant.VariantName}' binds "
                                 + $"{variant.Positional.Count} field(s), but "
                                 + $"'{variant.VariantName}' declares {fields.Count}.",
                            SourceName: ctx.SourceName,
                            SourceText: ctx.SourceText,
                            Span: variant.Span,
                            Label: fields.Count == 0
                                ? $"'{variant.VariantName}' has no fields"
                                : $"'{variant.VariantName}' declares: {string.Join(", ", fields)}",
                            Help: "bind fewer fields, or name them with `{ field }` to pick the "
                                + "ones you want."));
                    }

                    foreach (var named in variant.Named)
                    {
                        if (fields.Contains(named.Field, StringComparer.Ordinal)) { continue; }

                        ctx.Diagnostics.Add(new ToshDiagnostic(
                            Code: "tosh.bind.pattern_unknown_field",
                            Title: $"'{variant.VariantName}' has no field '{named.Field}'.",
                            SourceName: ctx.SourceName,
                            SourceText: ctx.SourceText,
                            Span: named.Span,
                            Label: fields.Count == 0
                                ? $"'{variant.VariantName}' has no fields"
                                : $"'{variant.VariantName}' declares: {string.Join(", ", fields)}",
                            Help: NearestFieldHelp(named.Field, fields)));
                    }
                }

                foreach (var element in variant.Positional) CheckPatternShape(element, ctx);
                foreach (var field in variant.Named) CheckPatternShape(field.Pattern, ctx);
                break;

            case ListPatternSyntax list:
                foreach (var element in list.Before) CheckPatternShape(element, ctx);
                foreach (var element in list.After) CheckPatternShape(element, ctx);
                break;

            case OrPatternSyntax alternatives:
                foreach (var alternative in alternatives.Alternatives)
                {
                    CheckPatternShape(alternative, ctx);
                }

                break;

            case BoundPatternSyntax bound:
                CheckPatternShape(bound.Pattern, ctx);
                break;
        }
    }

    /// <summary>Suggests the closest declared field, when one is close enough to mean it.</summary>
    private static string NearestFieldHelp(string written, IReadOnlyList<string> fields)
    {
        foreach (var field in fields)
        {
            if (field.StartsWith(written, StringComparison.OrdinalIgnoreCase) ||
                written.StartsWith(field, StringComparison.OrdinalIgnoreCase))
            {
                return $"did you mean '{field}'?";
            }
        }

        return "name a field the declaration has, or bind positionally with `(…)`.";
    }

    /// <summary>
    /// Declares what a pattern binds into the arm's scope, reporting shadowing — <c>TOAST-0053</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shadowing an outer variable is legal and sometimes what the author meant, so this is a
    /// warning rather than an error. What it prevents is the silent version: an arm that binds
    /// <c>count</c> over an outer <c>$count</c> reads the field everywhere in the arm, including
    /// the places that meant the outer one, and nothing says the name changed meaning.
    /// </para>
    /// <para>
    /// The names are also declared, which they were not before. Nothing depended on that — the
    /// unknown-variable check only speaks when it has a near match to suggest — but it left the
    /// binder able to flag a correct reference as a typo whenever an outer name sat one edit
    /// away from a bound one.
    /// </para>
    /// </remarks>
    private static void DeclarePatternBindings(ArgumentSyntax pattern, Context ctx)
    {
        switch (pattern)
        {
            case BarewordArgumentSyntax { Value: var name } bareword
                when name.Length > 0 && name != "_" && !name.StartsWith('$'):
                ReportShadowedBinding(name, bareword.Span, ctx);
                Declare(ctx, name);
                break;

            case VariantPatternSyntax variant:
                foreach (var element in variant.Positional)
                {
                    DeclarePatternBindings(element, ctx);
                }

                foreach (var field in variant.Named)
                {
                    DeclarePatternBindings(field.Pattern, ctx);
                }

                break;

            case ListPatternSyntax list:
                foreach (var element in list.Before)
                {
                    DeclarePatternBindings(element, ctx);
                }

                foreach (var element in list.After)
                {
                    DeclarePatternBindings(element, ctx);
                }

                if (list.HasRest && list.RestName.Length > 0)
                {
                    ReportShadowedBinding(list.RestName, list.Span, ctx);
                    Declare(ctx, list.RestName);
                }

                break;

            case OrPatternSyntax alternatives:
                // Every alternative binds the same names — the parser refuses otherwise — so
                // walking one of them declares the set without reporting the same name twice.
                if (alternatives.Alternatives.Count > 0)
                {
                    DeclarePatternBindings(alternatives.Alternatives[0], ctx);
                }

                break;

            case BoundPatternSyntax bound:
                ReportShadowedBinding(bound.Name, bound.Span, ctx);
                Declare(ctx, bound.Name);
                DeclarePatternBindings(bound.Pattern, ctx);
                break;
        }
    }

    private static void ReportShadowedBinding(string name, TextSpan span, Context ctx)
    {
        if (!IsKnown(ctx, name)) { return; }

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.bind.pattern_shadows_variable",
            Title: $"Pattern binding '{name}' shadows '${name}' from an enclosing scope.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: span,
            Label: $"'${name}' means the bound field for the rest of this arm",
            Help: $"rename the binding — `{{ field: inner{name} }}` names it something else — "
                + $"or write `_` if the arm does not use it. Shadowing is legal; this only says "
                + $"that '${name}' stops meaning what it meant outside.",
            Severity: ToshDiagnosticSeverity.Warning,
            Category: ToshDiagnosticCategory.Naming));
    }

    private static void CheckIdentifier(string name, TextSpan span, Context ctx)
    {
        if (IsKnown(ctx, name)) return;

        var suggestions = FindSuggestions(name, ctx);
        if (suggestions.Count == 0) return; // No near match; defer to runtime.

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.bind.unknown_variable",
            Title: $"Variable '${name}' is not declared in any enclosing scope.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: span,
            Label: BuildSuggestionLabel(suggestions),
            Help: "the binder flags variable references that look like typos for an in-scope name. " +
                  "If '$" + name + "' is meant to be set by an outer file (e.g. via 'source' or 'export'), " +
                  "either declare it in this source or set TOSH_DISABLE_BINDER=1 to suppress all binder checks."));
    }

    private static void CheckInterpolatedString(InterpolatedStringArgumentSyntax interp, Context ctx)
    {
        foreach (var part in interp.Parts)
        {
            if (part is InterpolatedStringExpressionPart expr)
            {
                foreach (Match m in InterpolationVariableRegex.Matches(expr.Expression))
                {
                    var name = m.Groups[1].Value;
                    // Compute the precise source span for the '$name'
                    // occurrence within the original interpolated literal.
                    // m.Index is relative to the trimmed expression text;
                    // expr.ExpressionSpan points at that text in the source.
                    var start = expr.ExpressionSpan.Start + m.Index;
                    var length = 1 + name.Length; // '$' + identifier
                    CheckIdentifier(name, new TextSpan(start, length), ctx);
                }
            }
        }
    }

    private static IReadOnlyList<string> FindSuggestions(string name, Context ctx)
    {
        var threshold = name.Length <= ShortNameMaxLength
            ? ShortNameLevenshteinThreshold
            : LongNameLevenshteinThreshold;

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in ctx.Scopes)
        {
            foreach (var n in scope) candidates.Add(n);
        }

        var scored = new List<(string Candidate, int Distance)>();
        foreach (var candidate in candidates)
        {
            if (Math.Abs(candidate.Length - name.Length) > threshold) continue;
            var distance = Levenshtein(name, candidate);
            if (distance <= threshold) scored.Add((candidate, distance));
        }

        return scored
            .OrderBy(s => s.Distance)
            .ThenBy(s => s.Candidate, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(s => s.Candidate)
            .ToArray();
    }

    private static string BuildSuggestionLabel(IReadOnlyList<string> suggestions)
    {
        return suggestions.Count switch
        {
            1 => $"did you mean '${suggestions[0]}'?",
            2 => $"did you mean '${suggestions[0]}' or '${suggestions[1]}'?",
            _ => "did you mean " + string.Join(", ", suggestions.Take(suggestions.Count - 1).Select(s => $"'${s}'"))
                 + $", or '${suggestions[^1]}'?",
        };
    }

    private static int Levenshtein(string source, string target)
    {
        if (source.Length == 0) return target.Length;
        if (target.Length == 0) return source.Length;

        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];

        for (var j = 0; j <= target.Length; j++) previous[j] = j;

        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private sealed record Context(
        string SourceName,
        string SourceText,
        List<HashSet<string>> Scopes,
        List<ToshDiagnostic> Diagnostics,
        IReadOnlyDictionary<string, IReadOnlyList<string>> PatternShapes,
        IReadOnlyDictionary<string, UnionShape> VariantUnions);
}

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

    /// <param name="ambientUnions">
    /// Unions the engine already knows that this source did not declare — the core prelude's
    /// <c>Option</c> and <c>Result</c>, and anything an import brought in. <c>TOAST-0083</c>:
    /// exhaustiveness was built from the source alone, so a `match` over `Result` was neither
    /// judged exhaustive nor reported incomplete, which left the two types whose entire purpose
    /// is exhaustive dispatch as the two without it.
    /// </param>
    public static IReadOnlyList<ToshDiagnostic> Bind(
        ParseResult parseResult,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ambientUnions = null,
        Func<string, bool>? isKnownTypeName = null)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var ctx = new Context(
            parseResult.SourceName,
            parseResult.SourceText,
            new List<HashSet<string>> { new(StringComparer.Ordinal) },
            new List<ToshDiagnostic>(),
            CollectPatternShapes(parseResult.Statement),
            CollectVariantUnions(parseResult.Statement, ambientUnions),
            CollectUnionsByName(parseResult.Statement, ambientUnions),
            isKnownTypeName,
            CollectDeclaredTypeNames(parseResult.Statement));

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
                CheckTypeTestTarget(oper, ctx);
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
                CheckBarewordVariantArms(match, ctx);
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
    /// Splits a variant pattern's name into the union that qualifies it, if any, and the variant
    /// itself — <c>TOAST-0095</c>. `Maybe.Some` is ("Maybe", "Some"); a bare `Some` is
    /// (null, "Some"). The path operator is already canonicalised to dots by the parser.
    /// </summary>
    private static void SplitVariantName(string name, out string? qualifier, out string member)
    {
        var separator = name.LastIndexOf('.');

        if (separator < 0)
        {
            qualifier = null;
            member = name;
            return;
        }

        qualifier = name[..separator];
        member = name[(separator + 1)..];
    }

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
    private static IReadOnlyDictionary<string, IReadOnlyList<UnionShape>> CollectVariantUnions(
        StatementSyntax statement,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ambientUnions)
    {
        var index = new Dictionary<string, List<UnionShape>>(StringComparer.Ordinal);

        void Add(UnionShape shape)
        {
            foreach (var variant in shape.Variants)
            {
                if (!index.TryGetValue(variant, out var candidates))
                {
                    candidates = new List<UnionShape>();
                    index[variant] = candidates;
                }

                if (!candidates.Any(existing => string.Equals(existing.Name, shape.Name, StringComparison.Ordinal)))
                {
                    candidates.Add(shape);
                }
            }
        }

        // Source declarations first, so they lead the candidate list and win the tie-break
        // below. That is the shadowing rule the rest of the language follows — a bare name is
        // where a declaration wins.
        foreach (var shape in CollectUnionDeclarations(statement))
        {
            Add(shape);
        }

        if (ambientUnions is not null)
        {
            foreach (var (unionName, variants) in ambientUnions)
            {
                Add(new UnionShape(unionName, variants));
            }
        }

        return index.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<UnionShape>)entry.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Which union an unqualified set of variant names refers to — <c>TOAST-0108</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A variant name identifies a union only while it is unique, and since <c>TOAST-0083</c>
    /// put <c>Option</c> and <c>Result</c> in the core prelude it very often is not: every
    /// <c>Some</c>, <c>None</c>, <c>Ok</c> and <c>Err</c> a user declares now collides with an
    /// ambient one.
    /// </para>
    /// <para>
    /// The arms disambiguate each other. Each names a variant, each variant has a candidate
    /// set, and the union being matched is in all of them — so the intersection is the answer
    /// whenever it is a single union. <c>Some</c> alone is ambiguous between <c>Option</c> and
    /// a user's <c>Maybe</c>; <c>Some</c> with <c>Nothing</c> is not.
    /// </para>
    /// <para>
    /// When the intersection still holds more than one, the source declaration wins — the same
    /// shadowing rule that ordered the candidates. Only a name that is ambiguous *between two
    /// source unions* gives up, because there the language itself has no answer and guessing
    /// would produce a diagnostic naming a union the author did not mean.
    /// </para>
    /// </remarks>
    private static UnionShape? ResolveUnionFromMembers(IReadOnlyList<string> members, Context ctx)
    {
        List<UnionShape>? candidates = null;

        foreach (var member in members)
        {
            if (!ctx.VariantUnions.TryGetValue(member, out var forMember))
            {
                return null;
            }

            if (candidates is null)
            {
                candidates = forMember.ToList();
                continue;
            }

            candidates.RemoveAll(shape =>
                !forMember.Any(other => string.Equals(other.Name, shape.Name, StringComparison.Ordinal)));
        }

        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        // The list is source-declared first, so the head is the shadowing winner. Two source
        // declarations sharing a variant name are genuinely ambiguous and are left alone.
        return candidates[0];
    }

    /// <summary>Every union declared in this source, in declaration order.</summary>
    private static IReadOnlyList<UnionShape> CollectUnionDeclarations(StatementSyntax statement)
    {
        var shapes = new List<UnionShape>();
        CollectUnionDeclarations(statement, shapes);
        return shapes;
    }

    private static void CollectUnionDeclarations(StatementSyntax statement, List<UnionShape> shapes)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) CollectUnionDeclarations(child, shapes);
                break;

            case UnionDefinitionStatementSyntax union:
                shapes.Add(new UnionShape(
                    union.Name,
                    union.Variants.Select(variant => variant.Name).ToArray()));
                break;
        }
    }

    /// <summary>
    /// The same unions, keyed by their own name — <c>TOAST-0095</c>.
    /// </summary>
    /// <remarks>
    /// The variant-keyed index cannot answer for a name two unions share: `Some` belongs to
    /// `Option` and to anything else that declares one, and the last collected wins. A
    /// *qualified* pattern names the union outright, so it is looked up here instead — which is
    /// what qualifying it is for, and the case the variant index was never able to serve.
    /// </remarks>
    private static IReadOnlyDictionary<string, UnionShape> CollectUnionsByName(
        StatementSyntax statement,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ambientUnions)
    {
        var byVariant = new Dictionary<string, UnionShape>(StringComparer.Ordinal);

        if (ambientUnions is not null)
        {
            foreach (var (unionName, variants) in ambientUnions)
            {
                byVariant[unionName] = new UnionShape(unionName, variants);
            }
        }

        // Source declarations override an ambient union of the same name.
        foreach (var shape in CollectUnionDeclarations(statement))
        {
            byVariant[shape.Name] = shape;
        }

        return byVariant;
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
        // Read every arm first. The union is resolved from the whole set rather than one arm at
        // a time — `TOAST-0108` — because a single variant name no longer identifies a union
        // now that `Option` and `Result` are ambient.
        var members = new List<string>();
        var unguarded = new List<string>();
        var guardedMembers = new List<string>();
        UnionShape? qualified = null;

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

                // `TOAST-0095`. An arm may name its variant qualified by the declaring union
                // (`Maybe.Some(v)`). Keyed bare, so the lookup missed and the whole check bailed
                // — a qualified match was neither judged exhaustive nor reported incomplete, and
                // said nothing at all.
                SplitVariantName(variant.VariantName, out var qualifier, out var member);

                if (qualifier is not null)
                {
                    // `TOAST-0095`. Resolved by union rather than by variant: the qualifier is
                    // the answer, not an extra condition on it.
                    if (!ctx.UnionsByName.TryGetValue(qualifier, out var owner) ||
                        !owner.Variants.Contains(member, StringComparer.Ordinal))
                    {
                        return;
                    }

                    if (qualified is null) { qualified = owner; }
                    else if (!string.Equals(qualified.Name, owner.Name, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                members.Add(member);
                (arm.Guard is null ? unguarded : guardedMembers).Add(member);
            }
        }

        if (members.Count == 0) { return; }

        // A qualified arm names the union outright; otherwise the arms disambiguate each other.
        var union = qualified ?? ResolveUnionFromMembers(members, ctx);

        if (union is null) { return; }

        // Every arm must name a variant of the union that was resolved. Without this a match
        // mixing two unions would be measured against whichever one won.
        if (members.Any(member => !union.Variants.Contains(member, StringComparer.Ordinal)))
        {
            return;
        }

        var covered = new HashSet<string>(unguarded, StringComparer.Ordinal);
        var guardedOnly = new HashSet<string>(guardedMembers, StringComparer.Ordinal);

        var missing = union.Variants.Where(name => !covered.Contains(name)).ToArray();

        if (missing.Length == 0)
        {
            // Every variant has an unguarded arm, so the top level is covered. What an arm
            // destructures *inside* a variant can still leave a gap — `TOAST-0054`.
            CheckNestedCoverage(match, union, ctx);
            return;
        }

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
    /// <summary>
    /// Every name this source declares that a type test could be qualified by.
    /// </summary>
    /// <remarks>
    /// Read from the syntax rather than from the engine, because that is the whole point: these
    /// are exactly the names the engine does not know yet at bind time.
    /// </remarks>
    private static IReadOnlySet<string> CollectDeclaredTypeNames(StatementSyntax statement)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectDeclaredTypeNames(statement, names);
        return names;
    }

    private static void CollectDeclaredTypeNames(
        StatementSyntax statement,
        HashSet<string> names,
        string prefix = "")
    {
        void Add(string name)
        {
            names.Add(name);

            if (prefix.Length > 0) { names.Add(prefix + name); }
        }

        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) { CollectDeclaredTypeNames(child, names, prefix); }
                break;

            case ModuleDefinitionStatementSyntax module:
                Add(module.Name);
                foreach (var child in module.Body.Statements)
                {
                    CollectDeclaredTypeNames(child, names, prefix + module.Name + ".");
                }

                break;

            case ClassDefinitionStatementSyntax @class: Add(@class.Name); break;
            case RecordDefinitionStatementSyntax record: Add(record.Name); break;
            case EnumDefinitionStatementSyntax @enum: Add(@enum.Name); break;
            case UnionDefinitionStatementSyntax union: Add(union.Name); break;
            case TypeAliasStatementSyntax alias: Add(alias.Name); break;
            case InterfaceDefinitionStatementSyntax @interface: Add(@interface.Name); break;
            case StructDefinitionStatementSyntax @struct: Add(@struct.Name); break;
        }
    }

    /// <summary>
    /// Reports <c>is</c> against a qualified name that resolves to no type — <c>TOAST-0105</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The complaint that opened <c>TOAST-0105</c> was that <c>is</c> cannot tell <em>no</em> from
    /// <em>I do not know</em>. Real types resolve now, but a misspelt one still answers
    /// <c>false</c> for exactly the same reason, so a typo in a type name is a silent wrong
    /// answer.
    /// </para>
    /// <para>
    /// Reported rather than raised. <c>is</c> stays total: making it throw would mean
    /// <c>if ($v is SomeOptionalType)</c> could no longer be written defensively, and every type
    /// test would become a possible throw site. The runtime answer is unchanged.
    /// </para>
    /// <para>
    /// Only the <em>qualified</em> spelling is checked, and only when the host supplied a
    /// resolver. A bare name has more ways to resolve — a CLR simple name, an alias, an import —
    /// and the binder has no types of its own; the probe comes from the engine the same way
    /// <c>isExecutableOnPath</c> and the ambient unions do.
    /// </para>
    /// </remarks>
    private static void CheckTypeTestTarget(OperatorArgumentSyntax oper, Context ctx)
    {
        if (ctx.IsKnownTypeName is null) { return; }
        if (oper.Operator is not ("is" or "is-not")) { return; }
        if (oper.Right is not StaticMemberAccessArgumentSyntax path) { return; }

        // Anything this source declares is invisible to the probe: the binder runs over the whole
        // script before a line of it executes, so a module declared here is not registered yet.
        // Measured — without this, `$c is Shapes.Circle` warned about a type that resolves.
        //
        // Qualified names are kept rather than only heads, so a module declared here can still
        // answer for a member it does *not* declare: `Shapes.Typo` is reportable precisely
        // because `Shapes` is known well enough to say what is in it.
        if (ctx.DeclaredTypeNames.Contains(path.Path)) { return; }

        var head = path.Path.Split('.')[0];
        var declaredLocally = ctx.DeclaredTypeNames.Contains(head);

        if (!declaredLocally && ctx.IsKnownTypeName(path.Path)) { return; }

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.bind.unknown_type_test",
            Title: $"'{path.Path}' does not name a type, so this test is always false.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: path.Span,
            Label: "no type by this name is in scope here",
            Help: "check the spelling, or the module qualifier. `is` answers false for a name it "
                + "cannot resolve rather than raising, so a mistyped type reads as a value that "
                + "simply is not of that type.",
            Severity: ToshDiagnosticSeverity.Warning,
            Category: ToshDiagnosticCategory.Naming));
    }

    /// <summary>
    /// Reports an arm written as a bare variant name beside arms that destructure the same
    /// union — <c>TOAST-0110</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bareword arm is a <em>string literal</em> pattern: <c>match ("hello") { hello => … }</c>
    /// matches, and that is a real feature. So <c>None</c> beside <c>Some(v)</c> compares the
    /// value against the text <c>"None"</c> and can never match an <c>Option</c> — silently, and
    /// with the exhaustiveness check bailing as well, because a non-variant arm means "not a
    /// union-shaped match".
    /// </para>
    /// <para>
    /// A unit variant is written <c>None()</c>. Since <c>TOAST-0083</c> put <c>Option</c> and
    /// <c>Result</c> in the prelude this is going to be one of the most common mistakes in the
    /// language, and nothing reported it.
    /// </para>
    /// <para>
    /// A <b>warning</b>, not an error, because the binder has no types: a value that is sometimes
    /// a union and sometimes the string <c>"None"</c> would make the arm meaningful. The trigger
    /// is narrow for the same reason — another arm in the same match must destructure a variant
    /// of the union the bareword names, so the author has already shown what they are matching.
    /// Matching a plain string against the bareword <c>Ok</c> is untouched.
    /// </para>
    /// </remarks>
    private static void CheckBarewordVariantArms(MatchArgumentSyntax match, Context ctx)
    {
        var members = new List<string>();

        foreach (var arm in match.Arms)
        {
            if (arm.Pattern is null) { continue; }

            foreach (var alternative in PatternAlternatives(arm.Pattern))
            {
                if (alternative is VariantPatternSyntax variant)
                {
                    SplitVariantName(variant.VariantName, out _, out var member);
                    members.Add(member);
                }
            }
        }

        if (members.Count == 0) { return; }

        var union = ResolveUnionFromMembers(members, ctx);
        if (union is null) { return; }

        foreach (var arm in match.Arms)
        {
            if (arm.IsWildcard || arm.Pattern is null) { continue; }

            foreach (var alternative in PatternAlternatives(arm.Pattern))
            {
                if (alternative is not BarewordArgumentSyntax { Value: var name } bareword) { continue; }
                if (name.Length == 0 || name == "_" || name.StartsWith('$')) { continue; }

                SplitVariantName(name, out _, out var bare);
                if (!union.Variants.Contains(bare, StringComparer.Ordinal)) { continue; }

                ctx.Diagnostics.Add(new ToshDiagnostic(
                    Code: "tosh.bind.bareword_variant_arm",
                    Title: $"Arm '{name}' matches the text \"{name}\", not the '{union.Name}' variant.",
                    SourceName: ctx.SourceName,
                    SourceText: ctx.SourceText,
                    Span: bareword.Span,
                    Label: $"'{bare}' is a variant of '{union.Name}', which another arm here destructures",
                    Help: $"write `{bare}()` to match the variant — a unit variant takes parentheses "
                        + $"like any other. A bareword arm is a string literal pattern, so quote it "
                        + $"as `\"{name}\"` if a string really is what this arm means.",
                    Severity: ToshDiagnosticSeverity.Warning,
                    Category: ToshDiagnosticCategory.Naming));
            }
        }
    }

    /// <summary>
    /// Reports a <c>match</c> whose arms cover every variant but not every *value* — the second
    /// slice of <c>TOAST-0054</c>.
    /// </summary>
    /// <remarks>
    /// Coverage was counted at the top level only, so <c>Add(Lit(a), r)</c> counted as covering
    /// all of <c>Add</c> and a nested value fell through at runtime. Guarded arms are left out of
    /// the matrix entirely rather than counted weakly: an arm that may not fire cannot complete a
    /// match, which is the rule the top-level check already applies.
    /// </remarks>
    private static void CheckNestedCoverage(MatchArgumentSyntax match, UnionShape union, Context ctx)
    {
        var rows = new List<List<MatchPattern>>();

        foreach (var arm in match.Arms)
        {
            if (arm.Guard is not null || arm.Pattern is null) { continue; }

            foreach (var lowered in LowerPattern(arm.Pattern, ctx))
            {
                rows.Add([lowered]);
            }
        }

        if (rows.Count == 0) { return; }

        var witness = FindWitness(rows, 1, ctx);

        if (witness is null || witness.Count == 0) { return; }

        var rendered = RenderWitness(witness[0]);

        // A bare `_` says only "something reaches here", which the top-level check has already
        // ruled out. Reporting it would be noise.
        if (rendered == "_") { return; }

        ctx.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.bind.match_not_exhaustive",
            Title: $"This match over '{union.Name}' covers every variant but not every value: "
                 + $"nothing matches {rendered}.",
            SourceName: ctx.SourceName,
            SourceText: ctx.SourceText,
            Span: match.Span,
            Label: $"'{union.Name}' declares: {string.Join(", ", union.Variants)}",
            Help: "an arm that destructures inside a variant covers only the shapes it names. "
                + "Add an arm for the shape above, widen one of the existing arms to a binding, "
                + "or `default`."));
    }

    // ── Nested coverage: usefulness over the pattern matrix (`TOAST-0054`) ────

    /// <summary>A pattern reduced to what coverage depends on: a constructor, or anything else.</summary>
    /// <remarks>
    /// Everything that is not a variant pattern becomes <see cref="MatchWildcard"/> — a literal, a
    /// comparison, a list pattern, a binding. That is the *sound* direction here. Treating an
    /// opaque pattern as matching everything can only make the check conclude "covered" when it is
    /// not, which is a missing report; treating it as a distinct constructor would make the check
    /// conclude "uncovered" when it is not, which is a false error on correct code. This item
    /// insists on never doing the second.
    /// </remarks>
    private abstract record MatchPattern;

    private sealed record MatchWildcard : MatchPattern
    {
        internal static readonly MatchWildcard Instance = new();
    }

    private sealed record MatchConstructor(string Variant, IReadOnlyList<MatchPattern> Arguments) : MatchPattern;

    /// <summary>
    /// The counterexample the algorithm builds: a value shape no arm matches.
    /// </summary>
    private static string RenderWitness(MatchPattern pattern) => pattern switch
    {
        MatchConstructor constructor when constructor.Arguments.Count == 0 => constructor.Variant + "()",
        MatchConstructor constructor =>
            constructor.Variant + "(" + string.Join(", ", constructor.Arguments.Select(RenderWitness)) + ")",
        _ => "_",
    };

    /// <summary>
    /// Lowers one arm pattern into the alternatives it stands for, or-patterns expanded.
    /// </summary>
    private static IReadOnlyList<MatchPattern> LowerPattern(ArgumentSyntax pattern, Context ctx)
    {
        switch (pattern)
        {
            case BoundPatternSyntax bound:
                return LowerPattern(bound.Pattern, ctx);

            case OrPatternSyntax or:
                var expanded = new List<MatchPattern>();
                foreach (var alternative in or.Alternatives)
                {
                    expanded.AddRange(LowerPattern(alternative, ctx));
                }

                return expanded.Count == 0 ? [MatchWildcard.Instance] : expanded;

            case VariantPatternSyntax variant:
                SplitVariantName(variant.VariantName, out _, out var member);

                // A named-field pattern needs the declared field order to be placed positionally.
                // Rather than guess it, the whole pattern becomes constructor-with-wildcards —
                // it still covers its constructor, just without the nested detail.
                if (variant.Named.Count > 0)
                {
                    return [new MatchConstructor(member, WildcardArguments(ArityOf(member, ctx)))];
                }

                var argumentAlternatives = new List<IReadOnlyList<MatchPattern>>();
                foreach (var positional in variant.Positional)
                {
                    argumentAlternatives.Add(LowerPattern(positional, ctx));
                }

                // Pad to the declared arity, so a pattern that names fewer fields than the variant
                // has still lines up column-for-column with one that names them all.
                var arity = Math.Max(ArityOf(member, ctx), variant.Positional.Count);
                while (argumentAlternatives.Count < arity)
                {
                    argumentAlternatives.Add([MatchWildcard.Instance]);
                }

                return CrossProduct(member, argumentAlternatives);

            default:
                return [MatchWildcard.Instance];
        }
    }

    private static IReadOnlyList<MatchPattern> WildcardArguments(int count) =>
        Enumerable.Repeat<MatchPattern>(MatchWildcard.Instance, count).ToArray();

    private static int ArityOf(string variant, Context ctx) =>
        ctx.PatternShapes.TryGetValue(variant, out var fields) ? fields.Count : 0;

    /// <summary>
    /// One row per combination of the arguments' alternatives, so a nested or-pattern becomes
    /// several rows the way a top-level one does.
    /// </summary>
    /// <remarks>
    /// Capped, because the product is exponential in the number of or-patterns in one arm. Past
    /// the cap the pattern collapses to constructor-with-wildcards, which over-states coverage
    /// and so can only lose a report — never invent one.
    /// </remarks>
    private static IReadOnlyList<MatchPattern> CrossProduct(
        string variant,
        List<IReadOnlyList<MatchPattern>> argumentAlternatives)
    {
        const int Cap = 256;

        var total = 1L;
        foreach (var alternatives in argumentAlternatives)
        {
            total *= alternatives.Count;
            if (total > Cap)
            {
                return [new MatchConstructor(variant, WildcardArguments(argumentAlternatives.Count))];
            }
        }

        var rows = new List<MatchPattern[]> { Array.Empty<MatchPattern>() };

        foreach (var alternatives in argumentAlternatives)
        {
            var next = new List<MatchPattern[]>(rows.Count * alternatives.Count);
            foreach (var prefix in rows)
            {
                foreach (var alternative in alternatives)
                {
                    next.Add([.. prefix, alternative]);
                }
            }

            rows = next;
        }

        return rows.Select(row => (MatchPattern)new MatchConstructor(variant, row)).ToArray();
    }

    /// <summary>
    /// Maranget's usefulness algorithm: a witness the matrix does not match, or null when the
    /// rows are exhaustive — <c>TOAST-0054</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The naive alternative — "a variant pattern with a refutable sub-pattern does not cover its
    /// variant" — is unsound in the direction that matters. A match with arms for
    /// <c>Add(Lit(a), r)</c>, <c>Add(Add(x, y), r)</c> and <c>Add(Neg(v), r)</c> *is* exhaustive,
    /// and the shortcut refuses it: a false error on exactly the compiler-shaped code the check
    /// exists to serve. This computes the answer instead.
    /// </para>
    /// <para>
    /// A column's union is resolved from the constructors that appear in it, by the same
    /// intersection <c>TOAST-0108</c> uses at the top level. Where no union can be resolved the
    /// signature is treated as never complete, which routes through the default matrix and reports
    /// only what the rows themselves fail to cover.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MatchPattern>? FindWitness(
        List<List<MatchPattern>> rows,
        int columns,
        Context ctx)
    {
        if (columns == 0)
        {
            // A witness exists exactly when nothing is left to match it.
            return rows.Count == 0 ? Array.Empty<MatchPattern>() : null;
        }

        var constructors = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row[0] is MatchConstructor constructor)
            {
                constructors[constructor.Variant] = Math.Max(
                    constructors.TryGetValue(constructor.Variant, out var seen) ? seen : 0,
                    constructor.Arguments.Count);
            }
        }

        var union = constructors.Count == 0
            ? null
            : ResolveUnionFromMembers(constructors.Keys.ToArray(), ctx);

        var complete = union is not null &&
            union.Variants.All(variant => constructors.ContainsKey(variant));

        if (complete)
        {
            foreach (var (variant, arity) in constructors)
            {
                var specialized = Specialize(rows, variant, arity);
                var witness = FindWitness(specialized, arity + columns - 1, ctx);

                if (witness is null) { continue; }

                var arguments = witness.Take(arity).ToArray();
                return [new MatchConstructor(variant, arguments), .. witness.Skip(arity)];
            }

            return null;
        }

        var defaulted = rows.Where(row => row[0] is not MatchConstructor)
            .Select(row => row.Skip(1).ToList())
            .ToList();

        var rest = FindWitness(defaulted, columns - 1, ctx);
        if (rest is null) { return null; }

        // Name a variant the rows never reach, when one is known; otherwise say only that
        // *something* reaches here.
        var missing = union?.Variants.FirstOrDefault(variant => !constructors.ContainsKey(variant));

        MatchPattern head = missing is null
            ? MatchWildcard.Instance
            : new MatchConstructor(missing, WildcardArguments(ArityOf(missing, ctx)));

        return [head, .. rest];
    }

    private static List<List<MatchPattern>> Specialize(
        List<List<MatchPattern>> rows,
        string variant,
        int arity)
    {
        var specialized = new List<List<MatchPattern>>();

        foreach (var row in rows)
        {
            switch (row[0])
            {
                case MatchConstructor constructor
                    when string.Equals(constructor.Variant, variant, StringComparison.Ordinal):
                    var head = new List<MatchPattern>(constructor.Arguments);
                    while (head.Count < arity) { head.Add(MatchWildcard.Instance); }
                    head.RemoveRange(arity, head.Count - arity);
                    head.AddRange(row.Skip(1));
                    specialized.Add(head);
                    break;

                case MatchConstructor:
                    // A different constructor: this row cannot match here.
                    break;

                default:
                    var widened = new List<MatchPattern>(WildcardArguments(arity));
                    widened.AddRange(row.Skip(1));
                    specialized.Add(widened);
                    break;
            }
        }

        return specialized;
    }

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
                SplitVariantName(variant.VariantName, out _, out var shapeMember);

                if (ctx.PatternShapes.TryGetValue(shapeMember, out var fields))
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
        IReadOnlyDictionary<string, IReadOnlyList<UnionShape>> VariantUnions,
        IReadOnlyDictionary<string, UnionShape> UnionsByName,
        Func<string, bool>? IsKnownTypeName,
        IReadOnlySet<string> DeclaredTypeNames);
}

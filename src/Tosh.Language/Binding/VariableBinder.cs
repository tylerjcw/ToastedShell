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
            new List<ToshDiagnostic>());

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
            if (stage is CommandSyntax command)
            {
                foreach (var arg in command.Arguments) VisitArgument(arg, ctx);
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
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is not null) VisitArgument(arm.Pattern, ctx);
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
        List<ToshDiagnostic> Diagnostics);
}

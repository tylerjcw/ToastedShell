using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Binding.BoundNodes;

/// <summary>
/// Root of the bound IR. Every node carries the original <see cref="TextSpan"/>
/// so diagnostics emitted during later passes (type checking, codegen) can
/// point back at the user's source.
/// </summary>
public abstract record BoundNode(TextSpan Span);

/// <summary>
/// A bound *expression* — anything that produces a value. Carries a
/// <see cref="BoundType"/> slot defaulting to <see cref="BoundType.Dynamic"/>.
/// </summary>
public abstract record BoundExpression(TextSpan Span, BoundType Type) : BoundNode(Span)
{
    /// <summary>
    /// Convenience for nodes whose type is set at construction time;
    /// overrides may capture a more specific type. Most lowered
    /// expressions stay <see cref="BoundType.Dynamic"/>.
    /// </summary>
    public BoundExpression WithType(BoundType type) => this with { Type = type };
}

/// <summary>
/// Catch-all wrapper for syntax nodes whose dedicated bound form has not
/// been carved out yet. Lets the lowering pass produce a complete
/// <see cref="BoundUnit"/> for any parse tree without forcing the IR to
/// model every shape on day one. Each carved-out bound node replaces a
/// previous use of this wrapper.
/// </summary>
/// <param name="Original">The parse-tree expression to evaluate dynamically.</param>
public sealed record BoundDynamicExpression(ArgumentSyntax Original, TextSpan Span)
    : BoundExpression(Span, BoundType.Dynamic);

/// <summary>
/// A bound *statement* — anything that doesn't produce a value, or whose
/// value is discarded. Statements have <see cref="BoundType.Void"/>
/// implicitly.
/// </summary>
public abstract record BoundStatement(TextSpan Span) : BoundNode(Span);

/// <summary>
/// Catch-all wrapper for statement nodes not yet modeled. Mirror of
/// <see cref="BoundDynamicExpression"/>.
/// </summary>
public sealed record BoundDynamicStatement(StatementSyntax Original, TextSpan Span)
    : BoundStatement(Span);

// ─── Statements ───────────────────────────────────────────────────────

/// <summary>A sequence of bound statements, one per top-level user line.</summary>
public sealed record BoundScript(IReadOnlyList<BoundStatement> Statements, TextSpan Span)
    : BoundStatement(Span);

/// <summary>A pipeline used as a statement (its values are emitted to the host).</summary>
public sealed record BoundPipelineStatement(BoundPipeline Pipeline, TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// A <c>var</c> declaration. <see cref="Value"/> is the initializer
/// pipeline (lowered); <see cref="Symbol"/> is the binding produced
/// for this declaration so subsequent references can resolve to it.
/// </summary>
public sealed record BoundVariableDeclaration(
    BoundSymbol Symbol,
    BoundPipeline? Value,
    bool IsConst,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

// ─── Expressions ──────────────────────────────────────────────────────

/// <summary>A literal value (number, string, bool, null).</summary>
public sealed record BoundLiteral(object? Value, TextSpan Span, BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>A reference to a variable resolved by the binder.</summary>
/// <param name="Name">The bare name (no leading $).</param>
/// <param name="Symbol">The resolved symbol; <c>null</c> means the binder
/// could not resolve it statically (e.g. <c>$env.HOME</c>, externally
/// sourced state) and runtime lookup is required.</param>
public sealed record BoundVariableReference(string Name, BoundSymbol? Symbol, TextSpan Span, BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// Member access: <c>$target.path</c>. <see cref="MemberPath"/> is
/// dotted (e.g. <c>"Config.Shell.Dirs"</c>); the binder does not split
/// it because the runtime walks each segment dynamically. NullSafe
/// captures <c>?.</c>.
/// </summary>
public sealed record BoundMemberAccess(
    BoundExpression Target,
    string MemberPath,
    bool NullSafe,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// A binary operator expression. v1 keeps the operator token as a
/// string; codegen will dispatch on it. Type inference fills the
/// <see cref="BoundExpression.Type"/> slot when both operands have
/// concrete numeric types.
/// </summary>
public sealed record BoundBinaryOperator(
    BoundExpression Left,
    string Operator,
    BoundExpression Right,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>A unary operator expression (<c>-x</c>, <c>!x</c>, etc.).</summary>
public sealed record BoundUnaryOperator(
    string Operator,
    BoundExpression Operand,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// A range expression: <c>start..end</c>, <c>start..end..step</c>, or
/// open-ended forms. Any side may be null (open range).
/// </summary>
public sealed record BoundRange(
    BoundExpression Start,
    BoundExpression? Step,
    BoundExpression? End,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

// ─── Pipelines ────────────────────────────────────────────────────────

/// <summary>
/// A bound pipeline. Stages run left-to-right and are connected by an
/// async-iterator handshake at runtime. Redirections and input
/// redirections are kept as raw syntax for now (rarely used; carve out
/// later when the IL backend needs them).
/// </summary>
public sealed record BoundPipeline(
    IReadOnlyList<BoundPipelineStage> Stages,
    PipelineSyntax Original,
    TextSpan Span)
    : BoundNode(Span);

public abstract record BoundPipelineStage(TextSpan Span) : BoundNode(Span);

/// <summary>
/// A command call site — the workhorse of every shell pipeline. The
/// resolved command reference is the v1 fast path; if it is null the
/// evaluator falls back to runtime registry lookup (matches the
/// existing <c>CommandSyntax.BoundCommand</c> field, but lifted into
/// the IR rather than mutated onto the parse tree).
/// </summary>
public sealed record BoundCommandCall(
    string Name,
    TextSpan NameSpan,
    IShellCommand? ResolvedCommand,
    IReadOnlyList<BoundArgument> Arguments,
    TextSpan Span)
    : BoundPipelineStage(Span);

/// <summary>
/// A pipeline stage that's just an expression (e.g. <c>42</c> or
/// <c>1..10</c> on its own line). Carrying a typed expression here
/// lets <see cref="TypeInferrer"/> propagate types through value
/// pipelines without modeling a synthetic command-call wrapper.
/// </summary>
public sealed record BoundExpressionStage(BoundExpression Value, TextSpan Span)
    : BoundPipelineStage(Span);

/// <summary>
/// An argument passed to a command. Named arguments preserve their
/// label; positional arguments leave <see cref="Name"/> null.
/// </summary>
public sealed record BoundArgument(
    string? Name,
    BoundExpression Value,
    bool IsSplat,
    TextSpan Span)
    : BoundNode(Span);

// ─── Symbols ──────────────────────────────────────────────────────────

/// <summary>
/// A resolved binding produced by the variable binder. Lives outside
/// the tree so multiple references can share a single symbol identity.
/// </summary>
public sealed record BoundSymbol(
    string Name,
    BoundSymbolKind Kind,
    int ScopeDepth,
    BoundType DeclaredType);

public enum BoundSymbolKind
{
    /// <summary>Declared with <c>var</c> in a local scope.</summary>
    LocalVariable,

    /// <summary>Function or lambda parameter.</summary>
    Parameter,

    /// <summary>For-loop induction variable.</summary>
    LoopVariable,

    /// <summary>Catch-clause exception binding.</summary>
    CatchVariable,

    /// <summary>A class property, accessed via <c>$this</c>.</summary>
    ClassProperty,

    /// <summary>A name brought in by destructuring (<c>var [a, b] = …</c>).</summary>
    Destructured,
}

// ─── Compilation unit ────────────────────────────────────────────────

/// <summary>
/// The output of the lowering pass: a bound script plus the symbol
/// table and the original parse result so the evaluator-on-IR can fall
/// back to syntax for not-yet-modeled cases.
/// </summary>
public sealed record BoundUnit(
    BoundScript Root,
    ParseResult ParseResult,
    IReadOnlyList<BoundSymbol> Symbols);

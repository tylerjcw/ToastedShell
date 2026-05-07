using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public abstract record ArgumentSyntax(TextSpan Span);

public sealed record BarewordArgumentSyntax(string Value, TextSpan Span) : ArgumentSyntax(Span);

public sealed record NamedArgumentSyntax(string Name, ArgumentSyntax Value, TextSpan Span) : ArgumentSyntax(Span);

public sealed record LiteralArgumentSyntax(object? Value, TextSpan Span) : ArgumentSyntax(Span);

public sealed record VariableReferenceArgumentSyntax(string Name, TextSpan Span) : ArgumentSyntax(Span);

public sealed record SplatArgumentSyntax(ArgumentSyntax Value, TextSpan Span) : ArgumentSyntax(Span);

public sealed record NewObjectArgumentSyntax(string TypeName, IReadOnlyList<ArgumentSyntax> Arguments, TextSpan Span) : ArgumentSyntax(Span);

public sealed record StaticMethodCallArgumentSyntax(
    string Path,
    IReadOnlyList<ArgumentSyntax> Arguments,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record StaticMemberAccessArgumentSyntax(string Path, TextSpan Span) : ArgumentSyntax(Span);

public sealed record ArrayLiteralArgumentSyntax(IReadOnlyList<ArgumentSyntax> Items, TextSpan Span) : ArgumentSyntax(Span);

public sealed record SpreadElementArgumentSyntax(ArgumentSyntax Value, TextSpan Span) : ArgumentSyntax(Span);

public abstract record RecordEntrySyntax(TextSpan Span);

public sealed record RecordFieldSyntax(string Name, ArgumentSyntax Value, TextSpan Span) : RecordEntrySyntax(Span);

public sealed record ComputedRecordFieldSyntax(ArgumentSyntax NameExpression, ArgumentSyntax Value, TextSpan Span) : RecordEntrySyntax(Span);

public sealed record SpreadRecordEntrySyntax(ArgumentSyntax Value, TextSpan Span) : RecordEntrySyntax(Span);

public sealed record RecordLiteralArgumentSyntax(IReadOnlyList<RecordEntrySyntax> Fields, TextSpan Span) : ArgumentSyntax(Span);

public sealed record DictEntrySyntax(ArgumentSyntax Key, ArgumentSyntax Value, TextSpan Span);

public sealed record DictLiteralArgumentSyntax(IReadOnlyList<DictEntrySyntax> Entries, TextSpan Span) : ArgumentSyntax(Span);

public sealed record FunctionReferenceArgumentSyntax(string Name, TextSpan Span) : ArgumentSyntax(Span);

public sealed record BlockArgumentSyntax(BlockSyntax Block, TextSpan Span) : ArgumentSyntax(Span);

/// <summary>
/// Represents <c>quote { expr }</c> — captures the argument's AST as a first-class value
/// instead of evaluating it. Used inside rune bodies for AST introspection.
/// </summary>
public sealed record QuoteArgumentSyntax(ArgumentSyntax Inner, TextSpan Span) : ArgumentSyntax(Span);

public sealed record AnonymousFunctionArgumentSyntax(
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    BlockSyntax Body,
    TextSpan Span,
    string? ReturnTypeName = null) : ArgumentSyntax(Span);

public sealed record MemberProjectionArgumentSyntax(IReadOnlyList<string> MemberPaths, TextSpan Span) : ArgumentSyntax(Span);

public sealed record MemberAccessArgumentSyntax(ArgumentSyntax Target, string MemberPath, TextSpan Span, bool NullSafe = false) : ArgumentSyntax(Span);

public sealed record IndexAccessArgumentSyntax(
    ArgumentSyntax Target,
    ArgumentSyntax Index,
    IndexLookupKind LookupKind,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record MethodCallArgumentSyntax(
    ArgumentSyntax Target,
    string MethodName,
    IReadOnlyList<ArgumentSyntax> Arguments,
    TextSpan Span,
    bool NullSafe = false) : ArgumentSyntax(Span);

public sealed record CallableInvocationArgumentSyntax(
    ArgumentSyntax Target,
    IReadOnlyList<ArgumentSyntax> Arguments,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record SubexpressionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

/// <summary>A `throw <expr>` used as an expression (e.g. `cond ? throw "x" : y`). Evaluating it raises.</summary>
public sealed record ThrowArgumentSyntax(ArgumentSyntax? Value, TextSpan Span) : ArgumentSyntax(Span);

public sealed record CommandSubstitutionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record InputProcessSubstitutionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record OutputProcessSubstitutionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record OperatorArgumentSyntax(
    ArgumentSyntax Left,
    string Operator,
    TextSpan OperatorSpan,
    ArgumentSyntax Right,
    TextSpan Span) : ArgumentSyntax(Span)
{
    /// <summary>
    /// Set by the lowering pass when both operands are constants and
    /// the operator is purely numeric/boolean/string. The evaluator
    /// short-circuits to this value instead of recursively evaluating
    /// <see cref="Left"/> and <see cref="Right"/>. Body-declared so it
    /// does not participate in record equality.
    /// </summary>
    public ConstantFold? FoldedConstant { get; set; }
}

/// <summary>
/// Cached folded value for a constant expression. The presence of
/// this object indicates the lowerer proved the operator's result.
/// </summary>
public sealed record ConstantFold(object? Value);

public sealed record ConditionalArgumentSyntax(
    ArgumentSyntax Condition,
    TextSpan QuestionSpan,
    ArgumentSyntax WhenTrue,
    TextSpan ColonSpan,
    ArgumentSyntax WhenFalse,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record IfExpressionArgumentSyntax(
    ArgumentSyntax Condition,
    BlockSyntax ThenBlock,
    BlockSyntax ElseBlock,
    TextSpan Span) : ArgumentSyntax(Span);

public abstract record MatchArmBodySyntax(TextSpan Span);

public sealed record MatchArmPipelineBodySyntax(PipelineSyntax Pipeline, TextSpan Span) : MatchArmBodySyntax(Span);

public sealed record MatchArmBlockBodySyntax(BlockSyntax Block, TextSpan Span) : MatchArmBodySyntax(Span);

public sealed record MatchArmSyntax(
    ArgumentSyntax? Pattern,
    ArgumentSyntax? Guard,
    MatchArmBodySyntax Body,
    bool IsWildcard,
    TextSpan Span);

public sealed record MatchArgumentSyntax(
    ArgumentSyntax Value,
    IReadOnlyList<MatchArmSyntax> Arms,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record UnaryOperatorArgumentSyntax(
    string Operator,
    TextSpan OperatorSpan,
    ArgumentSyntax Operand,
    TextSpan Span) : ArgumentSyntax(Span)
{
    /// <summary>
    /// Set by the lowering pass when the operand is a constant and the
    /// operator is purely numeric/boolean. See
    /// <see cref="OperatorArgumentSyntax.FoldedConstant"/>.
    /// </summary>
    public ConstantFold? FoldedConstant { get; set; }
}

public abstract record InterpolatedStringPart;
public sealed record InterpolatedStringLiteralPart(string Text) : InterpolatedStringPart;
/// <summary>
/// An interpolated <c>{expr}</c> hole inside a $"..." string.
/// <see cref="Expression"/> is the trimmed source text of the expression;
/// <see cref="ExpressionSpan"/> points at the expression characters in the
/// original source (between the opening <c>{</c> and closing <c>}</c>),
/// trimmed of surrounding whitespace so diagnostics can underline the exact
/// hole rather than the entire string literal.
/// </summary>
public sealed record InterpolatedStringExpressionPart(
    string Expression,
    TextSpan ExpressionSpan) : InterpolatedStringPart;

public sealed record InterpolatedStringArgumentSyntax(
    IReadOnlyList<InterpolatedStringPart> Parts,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record RangeArgumentSyntax(
    ArgumentSyntax Start,
    ArgumentSyntax? Step,
    ArgumentSyntax? End,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record NameOfArgumentSyntax(string Identifier, bool IsVariableReference, TextSpan Span) : ArgumentSyntax(Span);

public sealed record TupleLiteralArgumentSyntax(IReadOnlyList<ArgumentSyntax> Items, TextSpan Span) : ArgumentSyntax(Span);

public sealed record SetLiteralArgumentSyntax(IReadOnlyList<ArgumentSyntax> Items, TextSpan Span) : ArgumentSyntax(Span);

public sealed record ComparisonPatternSyntax(
    string Operator,
    TextSpan OperatorSpan,
    ArgumentSyntax Operand,
    TextSpan Span) : ArgumentSyntax(Span);

public abstract record RefinementDefinitionClauseSyntax(TextSpan Span);

public sealed record RefinementWhereClauseSyntax(
    ArgumentSyntax Predicate,
    TextSpan Span) : RefinementDefinitionClauseSyntax(Span);

public sealed record RefinementCoerceClauseSyntax(
    ArgumentSyntax? Guard,
    ArgumentSyntax Coercer,
    TextSpan Span) : RefinementDefinitionClauseSyntax(Span);

public sealed record RefinementClauseArgumentSyntax(
    IReadOnlyList<RefinementDefinitionClauseSyntax> Clauses,
    TextSpan Span) : ArgumentSyntax(Span);

// ── Comprehension syntax ──

/// Clause modifiers (`where` / `let`) are stored in declared order so evaluation
/// respects lexical intent — e.g. `let y = $x*2 where $y > 4` filters on the let binding.
public abstract record ComprehensionModifierSyntax(TextSpan Span);

public sealed record ComprehensionWhereSyntax(
    ArgumentSyntax Condition,
    TextSpan Span) : ComprehensionModifierSyntax(Span);

public sealed record ComprehensionLetSyntax(
    string VariableName,
    ArgumentSyntax Value,
    TextSpan Span) : ComprehensionModifierSyntax(Span);

public sealed record ComprehensionClauseSyntax(
    string VariableName,
    ArgumentSyntax Source,
    IReadOnlyList<ComprehensionModifierSyntax> Modifiers,
    ComprehensionClauseSyntax? InnerClause,
    TextSpan Span,
    /// <summary>
    /// Non-null for destructuring patterns like <c>for (a, b) in source</c>.
    /// Contains the real variable names; <see cref="VariableName"/> is a synthetic
    /// placeholder.
    /// </summary>
    IReadOnlyList<string>? DestructureNames = null,
    /// <summary>
    /// True when <see cref="InnerClause"/> was introduced by <c>||</c> and represents
    /// a parallel/zip binding rather than a nested Cartesian loop.
    /// </summary>
    bool InnerIsParallel = false);

public sealed record ListComprehensionArgumentSyntax(
    ArgumentSyntax Body,
    ComprehensionClauseSyntax Clause,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record SetComprehensionArgumentSyntax(
    ArgumentSyntax Body,
    ComprehensionClauseSyntax Clause,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record DictComprehensionArgumentSyntax(
    ArgumentSyntax Key,
    ArgumentSyntax Value,
    ComprehensionClauseSyntax Clause,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record GeneratorComprehensionArgumentSyntax(
    ArgumentSyntax Body,
    ComprehensionClauseSyntax Clause,
    TextSpan Span) : ArgumentSyntax(Span);

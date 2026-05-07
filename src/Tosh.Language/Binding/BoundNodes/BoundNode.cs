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
/// A reassignment to an existing variable: <c>$x = ...</c>,
/// <c>$x += 1</c>, etc. <see cref="Symbol"/> resolves the target if
/// the binder could find it locally; otherwise the runtime resolves
/// at execution time (e.g. globals introduced by <c>profile.tosh</c>).
/// </summary>
public sealed record BoundVariableAssignment(
    string Name,
    BoundSymbol? Symbol,
    string Operator,
    BoundPipeline Value,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// A lexical block: a sequence of bound statements that share a
/// single scope frame. Used as the body of <c>if</c>, <c>for</c>,
/// <c>while</c>, blocks passed to higher-order commands, function
/// bodies, etc.
/// </summary>
public sealed record BoundBlock(IReadOnlyList<BoundStatement> Statements, TextSpan Span)
    : BoundNode(Span);

/// <summary>An <c>if … else …</c> statement.</summary>
public sealed record BoundIfStatement(
    BoundExpression Condition,
    BoundBlock ThenBlock,
    BoundBlock? ElseBlock,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>A <c>for var in source { … }</c> loop.</summary>
public sealed record BoundForStatement(
    BoundSymbol LoopVariable,
    BoundPipeline Source,
    BoundBlock Body,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// A <c>while cond { … }</c> or <c>until cond { … }</c> loop. Until
/// loops invert the condition test; the IL emitter chooses the right
/// branch opcode.
/// </summary>
public sealed record BoundWhileStatement(
    BoundExpression Condition,
    BoundBlock Body,
    bool IsUntil,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary><c>break</c> out of the enclosing loop.</summary>
public sealed record BoundBreakStatement(TextSpan Span) : BoundStatement(Span);

/// <summary><c>continue</c> to the next iteration of the enclosing loop.</summary>
public sealed record BoundContinueStatement(TextSpan Span) : BoundStatement(Span);

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
    : BoundStatement(Span)
{
    /// <summary>
    /// True when the source wrote an explicit <c>: dynamic</c>
    /// annotation. Distinguishes intentional opt-out from the
    /// implicit-dynamic case (no annotation, dynamic-typed RHS),
    /// which the compile audit reports as a strict-mode violation.
    /// </summary>
    public bool AnnotatedDynamic { get; init; }
}

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

/// <summary>
/// One element in an array literal. <see cref="IsSpread"/> indicates
/// the parse-tree wrapped this with <c>...$xs</c> spread syntax — the
/// emitter will splice the inner enumerable in place rather than
/// adding it as a single element.
/// </summary>
public sealed record BoundArrayLiteralItem(BoundExpression Value, bool IsSpread, TextSpan Span)
    : BoundNode(Span);

/// <summary>An array literal: <c>[1, 2, ...$xs, 4]</c>.</summary>
public sealed record BoundArrayLiteral(
    IReadOnlyList<BoundArrayLiteralItem> Items,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>One segment of an interpolated string.</summary>
public abstract record BoundInterpolatedPart(TextSpan Span) : BoundNode(Span);

/// <summary>Literal text between <c>${…}</c> holes.</summary>
public sealed record BoundInterpolatedLiteral(string Text, TextSpan Span)
    : BoundInterpolatedPart(Span);

/// <summary>
/// An expression hole inside <c>$"…${expr}…"</c>. The original parser
/// captures the source text and re-parses it on demand; we keep the
/// raw source plus a lazily-lowered expression so the IL emitter can
/// either stamp a string conversion or fall back to runtime
/// re-parsing if the embedded expression isn't yet representable in
/// the bound IR.
/// </summary>
public sealed record BoundInterpolatedExpression(
    string SourceText,
    BoundExpression? Expression,
    TextSpan Span)
    : BoundInterpolatedPart(Span);

/// <summary>An interpolated string literal: <c>$"hello, $name!"</c>.</summary>
public sealed record BoundInterpolatedString(
    IReadOnlyList<BoundInterpolatedPart> Parts,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// A ternary <c>cond ? a : b</c> expression. Both branches are
/// expressions, so the result type is the join of their two types
/// (left dynamic for now).
/// </summary>
public sealed record BoundConditional(
    BoundExpression Condition,
    BoundExpression WhenTrue,
    BoundExpression WhenFalse,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// An <c>if cond { … } else { … }</c> used in expression position
/// (e.g. as a command argument). Both branches are required because
/// the value of an expressional <c>if</c> must be defined.
/// </summary>
public sealed record BoundIfExpression(
    BoundExpression Condition,
    BoundBlock ThenBlock,
    BoundBlock ElseBlock,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// A bare block passed as an argument: <c>where { $_ > 5 }</c>,
/// <c>each { ... }</c>, etc. The block has no formal parameters; the
/// host command supplies values via <c>$_</c> at runtime.
/// </summary>
/// <param name="Captures">
/// Local variables from enclosing scopes that are referenced inside
/// the block. The IL emitter materializes these as fields on a
/// generated closure type (or env-array, depending on strategy).
/// </param>
public sealed record BoundBlockExpression(
    BoundBlock Body,
    IReadOnlyList<BoundSymbol> Captures,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// One formal parameter of a lambda. <see cref="Default"/> is the
/// optional default-value pipeline, lowered in the *outer* scope so
/// captures inside it resolve correctly.
/// </summary>
public sealed record BoundParameter(
    string Name,
    BoundSymbol Symbol,
    BoundPipeline? Default,
    bool IsOptional,
    bool IsRest,
    TextSpan Span,
    string? TypeName = null)
    : BoundNode(Span);

/// <summary>
/// An anonymous function: <c>{|x, y| $x + $y}</c> or its
/// <c>fn(x) => …</c> equivalent. Parameters are bound inside a fresh
/// scope; <see cref="Captures"/> records non-parameter, non-local
/// names that the body references.
/// </summary>
public sealed record BoundLambda(
    IReadOnlyList<BoundParameter> Parameters,
    BoundBlock Body,
    IReadOnlyList<BoundSymbol> Captures,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// Invoking a callable expression — <c>$fn(1, 2)</c> or
/// <c>(some-callable)(arg)</c>. v1 keeps the argument list flat;
/// named/splat handling matches <see cref="BoundCommandCall"/>.
/// </summary>
public sealed record BoundCallableInvocation(
    BoundExpression Target,
    IReadOnlyList<BoundArgument> Arguments,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

// ─── Phase C-1: escape hatches (try/throw/return/match/switch) ────────

/// <summary>
/// <c>return [pipeline]</c>. The optional value is lowered as a
/// pipeline (the same shape as <c>VariableDeclaration</c>'s
/// initializer) so the IL emitter can hand it off verbatim. A null
/// value means a bare <c>return</c>.
/// </summary>
public sealed record BoundReturnStatement(BoundPipeline? Value, TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// <c>throw [pipeline]</c> as a statement. A null value re-throws the
/// currently-handled exception (matches the parser's permissiveness).
/// </summary>
public sealed record BoundThrowStatement(BoundPipeline? Value, TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// <c>throw expr</c> in expression position (e.g. inside a ternary).
/// Evaluating raises the exception; the IL emitter must flag this
/// expression as never returning so basic-block dead-code analysis is
/// correct.
/// </summary>
public sealed record BoundThrowExpression(BoundExpression? Value, TextSpan Span, BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// One <c>catch</c> clause attached to a <see cref="BoundTryStatement"/>.
/// <see cref="Variable"/> is the symbol the exception binds to, or
/// null if the parser produced <c>catch { ... }</c> with no name.
/// </summary>
public sealed record BoundCatchClause(
    BoundSymbol? Variable,
    BoundBlock Body,
    TextSpan Span)
    : BoundNode(Span);

/// <summary>
/// <c>try { … } catch [(name)] { … } finally { … }</c>. At least one
/// of <see cref="Catch"/> / <see cref="Finally"/> is non-null (the
/// parser enforces this).
/// </summary>
public sealed record BoundTryStatement(
    BoundBlock TryBlock,
    BoundCatchClause? Catch,
    BoundBlock? Finally,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// One arm of a <see cref="BoundMatchExpression"/>. v1 keeps the
/// pattern as a raw <see cref="BoundExpression"/> (the runtime tests
/// it dynamically); refined pattern shapes can be carved later.
/// <see cref="Pattern"/> is null for the wildcard arm.
/// </summary>
public sealed record BoundMatchArm(
    BoundExpression? Pattern,
    BoundExpression? Guard,
    BoundBlock Body,
    bool IsWildcard,
    TextSpan Span)
    : BoundNode(Span);

/// <summary>
/// <c>match $x { 1 =&gt; … ; default =&gt; … }</c> in expression
/// position. The IL emitter compiles this as a chained series of
/// equality (or pattern-test) branches.
/// </summary>
public sealed record BoundMatchExpression(
    BoundExpression Value,
    IReadOnlyList<BoundMatchArm> Arms,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// One <c>case</c> arm of a <see cref="BoundSwitchStatement"/>.
/// <see cref="Pattern"/> is the value/pattern expression to test
/// against, with an optional <see cref="Guard"/>.
/// </summary>
public sealed record BoundSwitchCase(
    BoundExpression Pattern,
    BoundExpression? Guard,
    BoundBlock Body,
    TextSpan Span)
    : BoundNode(Span);

/// <summary>
/// <c>switch ($x) { case … { } default { } }</c>. Switch bodies are
/// statements (no value); the IL emitter compiles to a chain or to
/// the IL <c>switch</c> opcode where applicable.
/// </summary>
public sealed record BoundSwitchStatement(
    BoundExpression Value,
    IReadOnlyList<BoundSwitchCase> Cases,
    BoundBlock? Default,
    TextSpan Span)
    : BoundStatement(Span);

// ─── Phase C-2: types & object access ─────────────────────────────────

/// <summary>
/// <c>new TypeName(args)</c>. The runtime resolves <c>TypeName</c> at
/// evaluation time today; the IL emitter will eventually look up a
/// constructor by signature using the captured argument types.
/// </summary>
public sealed record BoundNewObject(
    string TypeName,
    IReadOnlyList<BoundArgument> Arguments,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// Instance method invocation: <c>$target.Method(args)</c>.
/// <see cref="NullSafe"/> matches the parser's <c>?.</c> form.
/// </summary>
public sealed record BoundMethodCall(
    BoundExpression Target,
    string MethodName,
    IReadOnlyList<BoundArgument> Arguments,
    bool NullSafe,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// Static method invocation: <c>Math.Sqrt(2)</c>. <see cref="Path"/>
/// is the dotted name (the parser does not split it).
/// </summary>
public sealed record BoundStaticMethodCall(
    string Path,
    IReadOnlyList<BoundArgument> Arguments,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// Static member read: <c>Math.PI</c>, <c>String.Empty</c>.
/// <see cref="Path"/> is the dotted name.
/// </summary>
public sealed record BoundStaticMemberAccess(string Path, TextSpan Span, BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// Indexer or key lookup: <c>$arr[0]</c>, <c>$dict["key"]</c>.
/// <see cref="LookupKind"/> distinguishes integer indexing, string
/// keys, slices, etc. (matches <see cref="Tosh.Language.Parsing.IndexLookupKind"/>).
/// </summary>
public sealed record BoundIndexAccess(
    BoundExpression Target,
    BoundExpression Index,
    IndexLookupKind LookupKind,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// Member or indexed assignment: <c>$obj.x = …</c>, <c>$arr[0] = …</c>,
/// <c>$obj.x += …</c>. The target is a BoundExpression; the runtime
/// (and eventual IL emitter) inspects its shape to dispatch
/// property/field/indexer setters appropriately.
/// </summary>
public sealed record BoundMemberAssignment(
    BoundExpression Target,
    string Operator,
    BoundPipeline Value,
    TextSpan Span)
    : BoundStatement(Span);

// ─── Phase C-3: declarations, deferred control flow, niche literals ───

/// <summary>
/// <c>defer { … }</c>. Conceptually lowers to a try/finally where
/// the body runs at scope exit. We keep the dedicated node so the
/// IL emitter can choose its own desugaring strategy (single-finally
/// at function exit, per-scope, etc.).
/// </summary>
public sealed record BoundDeferStatement(BoundBlock Body, TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// <c>yield [pipeline]</c>. The IL emitter compiles this as part of
/// an iterator state machine. A null value is a bare <c>yield</c>
/// (whose semantics in Tosh today are the same as <c>return null</c>
/// in iterator context).
/// </summary>
public sealed record BoundYieldStatement(BoundPipeline? Value, TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// <c>using Module [as Alias]</c> — a namespace-import statement.
/// Today the runtime registers the import into the engine's import
/// table; the bound node carries the structured form so codegen can
/// stamp the same effect.
/// </summary>
public sealed record BoundUsingStatement(
    string Target,
    string? Alias,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// One side of a <c>($a, $b) = …</c> assignment.
/// </summary>
public sealed record BoundTupleAssignment(
    IReadOnlyList<string> Names,
    BoundPipeline Value,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>Pattern shape for destructuring declarations.</summary>
public abstract record BoundDestructuringPattern(TextSpan Span) : BoundNode(Span);

/// <summary><c>var [a, b] = …</c>.</summary>
public sealed record BoundArrayDestructuringPattern(
    IReadOnlyList<BoundSymbol> Symbols,
    TextSpan Span)
    : BoundDestructuringPattern(Span);

/// <summary><c>var { a, b } = …</c>.</summary>
public sealed record BoundRecordDestructuringPattern(
    IReadOnlyList<BoundSymbol> Symbols,
    TextSpan Span)
    : BoundDestructuringPattern(Span);

/// <summary>A <c>var</c> with destructuring on the left.</summary>
public sealed record BoundDestructuringDeclaration(
    BoundDestructuringPattern Pattern,
    BoundPipeline Value,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// <c>alloc name = …</c>. v1 IL keeps this dynamic (native interop),
/// but the carve-out lets later phases reason about the name binding
/// without a syntax dependency.
/// </summary>
public sealed record BoundAllocStatement(
    string Name,
    BoundPipeline Value,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>
/// <c>func name(params) { … }</c> definition. Lowering treats this
/// as a top-level lambda binding: the body is a fresh
/// <see cref="BoundBlock"/>, parameters get
/// <see cref="BoundSymbolKind.Parameter"/> bindings, and the
/// <see cref="Symbol"/> produced for the function name is what
/// later <c>$name</c> references resolve to. <see cref="Captures"/>
/// is recorded in case a function is declared inside another scope
/// (e.g. inside a class method body).
/// </summary>
public sealed record BoundFunctionDefinition(
    string Name,
    BoundSymbol Symbol,
    IReadOnlyList<BoundParameter> Parameters,
    string? ReturnTypeName,
    BoundBlock Body,
    IReadOnlyList<BoundSymbol> Captures,
    bool IsCommandWrapper,
    DeclarationModifier Modifier,
    TextSpan Span,
    BoundType? ReturnType = null)
    : BoundStatement(Span);

/// <summary>
/// <c>rune name(params) { … }</c> — like a function but eagerly
/// invoked at definition site. Same lowering shape as
/// <see cref="BoundFunctionDefinition"/>.
/// </summary>
public sealed record BoundRuneDefinition(
    string Name,
    BoundSymbol Symbol,
    IReadOnlyList<BoundParameter> Parameters,
    BoundBlock Body,
    IReadOnlyList<BoundSymbol> Captures,
    bool IsSealed,
    bool IsFixed,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

// ─── Class-family declarations ──

/// <summary>Common base for class / struct members.</summary>
public abstract record BoundClassMember(TextSpan Span) : BoundNode(Span);

/// <summary>
/// One property/field of a class or struct: <c>prop X = init</c>.
/// Initializer + optional getter/setter bodies are bound. Visibility
/// flags are surfaced verbatim so the IL emitter can stamp them.
/// </summary>
public sealed record BoundClassPropertyMember(
    string Name,
    string? TypeName,
    BoundPipeline? Initializer,
    BoundBlock? GetterBody,
    BoundBlock? SetterBody,
    bool IsShy,
    bool IsStatic,
    bool IsFixed,
    bool IsVital,
    bool IsGuarded,
    bool IsLazy,
    bool IsFading,
    bool IsLocal,
    bool IsAbstract,
    TextSpan Span)
    : BoundClassMember(Span);

/// <summary>One method of a class or struct.</summary>
public sealed record BoundClassMethodMember(
    BoundFunctionDefinition Method,
    bool IsStatic,
    bool IsShy,
    bool IsAbstract,
    bool IsOverride,
    bool IsGuarded,
    bool IsFading,
    bool IsLocal,
    bool IsRaw,
    TextSpan Span)
    : BoundClassMember(Span);

/// <summary>A class constructor (separate from the primary ctor params).</summary>
public sealed record BoundClassConstructorMember(
    IReadOnlyList<BoundParameter> Parameters,
    BoundBlock Body,
    TextSpan Span)
    : BoundClassMember(Span);

/// <summary>An event member declared inside a class: <c>event OnName: PayloadType</c>.</summary>
public sealed record BoundClassEventMember(
    string Name,
    string? PayloadTypeName,
    bool IsShy,
    TextSpan Span)
    : BoundClassMember(Span);

/// <summary><c>class Name(primaryCtor) : Base { … }</c>.</summary>
public sealed record BoundClassDefinition(
    string Name,
    IReadOnlyList<BoundParameter> PrimaryConstructorParameters,
    IReadOnlyList<BoundClassMember> Members,
    string? BaseClassName,
    IReadOnlyList<BoundPipeline>? BaseConstructorArgs,
    IReadOnlyList<string>? ImplementedInterfaces,
    IReadOnlyList<string>? UsedTraits,
    bool IsSealed,
    bool IsAbstract,
    bool IsHermit,
    bool IsStrict,
    bool IsPartial,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

/// <summary>One method signature on an interface.</summary>
public sealed record BoundInterfaceMethodSignature(
    string Name,
    IReadOnlyList<BoundParameter> Parameters,
    string? ReturnTypeName,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundInterfaceDefinition(
    string Name,
    IReadOnlyList<BoundInterfaceMethodSignature> Methods,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundUnionVariant(
    string Name,
    IReadOnlyList<BoundParameter> Fields,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundUnionDefinition(
    string Name,
    IReadOnlyList<BoundUnionVariant> Variants,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundEnumMember(
    string Name,
    BoundPipeline? Value,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundEnumDefinition(
    string Name,
    string? UnderlyingTypeName,
    IReadOnlyList<BoundEnumMember> Members,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundRecordFieldDefinition(
    string Name,
    string? TypeName,
    BoundPipeline? DefaultValue,
    bool IsOptional,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundRecordDefinition(
    string Name,
    IReadOnlyList<BoundRecordFieldDefinition> Fields,
    bool IsSealed,
    bool IsStrict,
    bool IsPartial,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundStructDefinition(
    string Name,
    IReadOnlyList<BoundRecordFieldDefinition> Fields,
    IReadOnlyList<BoundClassMember> Members,
    bool IsSealed,
    bool IsFluid,
    bool IsPartial,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundTraitMethodSignature(
    string Name,
    IReadOnlyList<BoundParameter> Parameters,
    string? ReturnTypeName,
    BoundBlock? DefaultBody,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundTraitPropertySignature(
    string Name,
    string? TypeName,
    BoundPipeline? DefaultValue,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundTraitDefinition(
    string Name,
    IReadOnlyList<BoundTraitMethodSignature> Methods,
    IReadOnlyList<BoundTraitPropertySignature> Properties,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundEventFieldDefinition(
    string Name,
    string? TypeName,
    BoundPipeline? DefaultValue,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundEventDefinition(
    string Name,
    IReadOnlyList<BoundEventFieldDefinition> Fields,
    bool IsRequired,
    bool IsLocal,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundModuleDefinition(
    string Name,
    BoundBlock Body,
    DeclarationModifier Modifier,
    TextSpan Span,
    bool IsPartial = false)
    : BoundStatement(Span);

public sealed record BoundSubcommandStatement(
    string Name,
    SubcommandModifier Modifiers,
    BoundBlock Body,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundScriptInputStatement(
    ScriptInputDeclarationKind Kind,
    IReadOnlyList<BoundParameter> Parameters,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundTypeAliasStatement(
    string Name,
    IReadOnlyList<string> TypeParameters,
    string BaseTypeName,
    BoundExpression? Refinement,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundRequireImport(
    string Name,
    string? Alias,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundRequireStatement(
    string Target,
    IReadOnlyList<BoundRequireImport> Imports,
    bool IsNative,
    string? Alias,
    DeclarationModifier Modifier,
    TextSpan Span)
    : BoundStatement(Span);

public sealed record BoundNativeFunctionParameter(
    string Name,
    string? TypeName,
    NativeParameterPassingMode PassingMode,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundNativeFunctionBinding(
    string Name,
    string SymbolName,
    IReadOnlyList<BoundNativeFunctionParameter> Parameters,
    string? ReturnTypeName,
    string? CallingConventionName,
    TextSpan Span)
    : BoundNode(Span);

public sealed record BoundBindStatement(
    string ModuleName,
    string? NativeTarget,
    IReadOnlyList<BoundNativeFunctionBinding> Functions,
    TextSpan Span)
    : BoundStatement(Span);

// ─── Phase C-3: niche expressions ──────────────────────────────────────

/// <summary>One entry of a record literal.</summary>
public abstract record BoundRecordEntry(TextSpan Span) : BoundNode(Span);

public sealed record BoundRecordField(string Name, BoundExpression Value, TextSpan Span)
    : BoundRecordEntry(Span);

public sealed record BoundComputedRecordField(BoundExpression NameExpression, BoundExpression Value, TextSpan Span)
    : BoundRecordEntry(Span);

public sealed record BoundRecordSpreadEntry(BoundExpression Value, TextSpan Span)
    : BoundRecordEntry(Span);

/// <summary><c>{ name: "x", age: 1, ...$rest }</c>.</summary>
public sealed record BoundRecordLiteral(
    IReadOnlyList<BoundRecordEntry> Fields,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

public sealed record BoundDictEntry(
    BoundExpression Key,
    BoundExpression Value,
    TextSpan Span)
    : BoundNode(Span);

/// <summary><c>{ "k1" => v1, "k2" => v2 }</c>.</summary>
public sealed record BoundDictLiteral(
    IReadOnlyList<BoundDictEntry> Entries,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

public sealed record BoundSetLiteral(
    IReadOnlyList<BoundExpression> Items,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

public sealed record BoundTupleLiteral(
    IReadOnlyList<BoundExpression> Items,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>(cmd args)</c> — captures stdout of the inner pipeline.</summary>
public sealed record BoundCommandSubstitution(
    BoundPipeline Pipeline,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>$(pipeline)</c> / <c>(pipeline)</c> — produces the
/// pipeline's value(s) as a single materialised result.</summary>
public sealed record BoundSubexpression(
    BoundPipeline Pipeline,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>&lt;(pipeline)</c> — process input substitution.</summary>
public sealed record BoundInputProcessSubstitution(
    BoundPipeline Pipeline,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>&gt;(pipeline)</c> — process output substitution.</summary>
public sealed record BoundOutputProcessSubstitution(
    BoundPipeline Pipeline,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>quote { … }</c> — captures argument's AST as a value.</summary>
public sealed record BoundQuoteExpression(
    ArgumentSyntax Inner,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>nameof(x)</c> / <c>nameof(\$x)</c>.</summary>
public sealed record BoundNameOfExpression(
    string Identifier,
    bool IsVariableReference,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>&amp;funcname</c> — first-class function reference.</summary>
public sealed record BoundFunctionReference(
    string Name,
    BoundSymbol? Symbol,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary><c>_.Name</c> projection used in pipelines.</summary>
public sealed record BoundMemberProjection(
    IReadOnlyList<string> MemberPaths,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

/// <summary>
/// <c>&gt; 5</c> / <c>=~ "regex"</c> match-arm pattern. Carved as
/// expression so it can flow through the same pattern-test path as
/// other arms; the runtime tests it specially.
/// </summary>
public sealed record BoundComparisonPattern(
    string Operator,
    BoundExpression Operand,
    TextSpan Span,
    BoundType Type)
    : BoundExpression(Span, Type);

// ─── Pipelines ────────────────────────────────────────────────────────

/// <summary>
/// A bound pipeline. Stages run left-to-right and are connected by an
/// async-iterator handshake at runtime. Bound redirections carry
/// fully lowered <see cref="BoundExpression"/> targets so the IL
/// backend can emit them without source replay.
/// </summary>
public sealed record BoundPipeline(
    IReadOnlyList<BoundPipelineStage> Stages,
    PipelineSyntax Original,
    TextSpan Span)
    : BoundNode(Span)
{
    public IReadOnlyList<BoundRedirection> BoundRedirections { get; init; } = Array.Empty<BoundRedirection>();
    public BoundInputRedirection? BoundInputRedirection { get; init; }
}

/// <summary>
/// A redirection of a pipeline's stdout/stderr stream to a file
/// path. <see cref="Target"/> is the lowered expression that
/// evaluates (at runtime) to the file path string.
/// </summary>
public sealed record BoundRedirection(
    Parsing.RedirectionStream Stream,
    Parsing.RedirectionMode Mode,
    BoundExpression Target,
    TextSpan Span)
    : BoundNode(Span);

/// <summary>
/// A redirection of a pipeline's stdin from a file path. <see
/// cref="Source"/> is the lowered expression that evaluates to the
/// file path string.
/// </summary>
public sealed record BoundInputRedirection(
    BoundExpression Source,
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
    : BoundPipelineStage(Span)
{
    /// <summary>
    /// When the Lowerer resolves a call to a same-source overloaded function
    /// by arity, this is the zero-based index into the declaration-order list
    /// of overloads with that name.  <c>null</c> means unresolved (runtime
    /// dispatch) or a non-overloaded function.
    /// </summary>
    public int? OverloadIndex { get; init; }
}

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

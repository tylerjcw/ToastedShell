using Tosh.Compiler.IR;
using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public abstract record StatementSyntax(TextSpan Span);

public sealed record ScriptStatementSyntax(IReadOnlyList<StatementSyntax> Statements, TextSpan Span, DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record PipelineStatementSyntax(PipelineSyntax Pipeline, TextSpan Span) : StatementSyntax(Span);

public sealed record VariableDeclarationStatementSyntax(string Name, string? TypeName, PipelineSyntax? Value, DeclarationModifier Modifier, bool IsConst, TextSpan Span, ArgumentSyntax? Refinement = null) : StatementSyntax(Span);

public abstract record DestructuringPatternSyntax(TextSpan Span);

public sealed record ArrayDestructuringPatternSyntax(IReadOnlyList<string> Names, TextSpan Span) : DestructuringPatternSyntax(Span);

public sealed record RecordDestructuringPatternSyntax(IReadOnlyList<string> Names, TextSpan Span) : DestructuringPatternSyntax(Span);

/// <summary>Destructuring declaration: <c>var [a, b] = …</c>, <c>var (a, b) = …</c>, <c>var { a, b } = …</c>.</summary>
/// <param name="IsConst">
/// Whether the keyword was <c>const</c>. Without this the bindings were declared mutable
/// however the declaration was spelled, so <c>const [A, B] = [1, 2]</c> then <c>$A = 9</c>
/// succeeded — <c>const</c> was accepted and then ignored (<c>TS-P2-59</c>).
/// </param>
public sealed record DestructuringDeclarationStatementSyntax(DestructuringPatternSyntax Pattern, PipelineSyntax Value, DeclarationModifier Modifier, bool IsConst, TextSpan Span) : StatementSyntax(Span);

public sealed record AllocStatementSyntax(string Name, PipelineSyntax Value, DeclarationModifier Modifier, TextSpan Span) : StatementSyntax(Span);

public sealed record VariableAssignmentStatementSyntax(string Name, string Operator, PipelineSyntax Value, TextSpan Span) : StatementSyntax(Span);

public sealed record MemberAssignmentStatementSyntax(ArgumentSyntax Target, string Operator, PipelineSyntax Value, TextSpan Span) : StatementSyntax(Span);

public sealed record ReturnStatementSyntax(PipelineSyntax? Value, TextSpan Span) : StatementSyntax(Span);

public sealed record YieldStatementSyntax(PipelineSyntax? Value, TextSpan Span) : StatementSyntax(Span);

/// <summary>
/// Represents tuple unpacking assignment, e.g. ($a, $b) = ($b, $a)
/// </summary>
public sealed record TupleAssignmentStatementSyntax(
    IReadOnlyList<string> LeftNames,
    PipelineSyntax Value,
    TextSpan Span) : StatementSyntax(Span);

public sealed record BreakStatementSyntax(TextSpan Span) : StatementSyntax(Span);

public sealed record ContinueStatementSyntax(TextSpan Span) : StatementSyntax(Span);

public sealed record UsingStatementSyntax(string Target, string? Alias, DeclarationModifier Modifier, TextSpan Span) : StatementSyntax(Span);

public sealed record TypeAliasStatementSyntax(
    string Name,
    IReadOnlyList<string> TypeParameters,
    string BaseTypeName,
    ArgumentSyntax? Refinement,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record RequireImportSyntax(string Name, string? Alias, TextSpan Span);

public sealed record RequireStatementSyntax(
    string Target,
    IReadOnlyList<RequireImportSyntax> Imports,
    bool IsNative,
    string? Alias,
    DeclarationModifier Modifier,
    TextSpan Span) : StatementSyntax(Span);

public sealed record FunctionParameterSyntax(string Name, string? TypeName, bool IsOptional, bool IsRest, PipelineSyntax? DefaultValue, TextSpan Span, ArgumentSyntax? Refinement = null, string? Description = null);

public sealed record ScriptInputStatementSyntax(
    ScriptInputDeclarationKind Kind,
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record SubcommandStatementSyntax(
    string Name,
    SubcommandModifier Modifiers,
    BlockSyntax Body,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record NativeFunctionParameterSyntax(
    string Name,
    string? TypeName,
    NativeParameterPassingMode PassingMode,
    TextSpan Span);

public sealed record FunctionDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    string? ReturnTypeName,
    BlockSyntax Body,
    bool IsCommandWrapper,
    DeclarationModifier Modifier,
    TextSpan Span,
    string? HandlesEvent = null,
    int? HandlerPriority = null,
    bool IsOnceHandler = false,
    BlockSyntax? WhenGuard = null,
    DocComment? DocComment = null,
    IReadOnlyList<string>? TypeParameters = null,
    IReadOnlyList<TypeParameterConstraintSyntax>? TypeParameterConstraints = null) : StatementSyntax(Span);

public sealed record RuneDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    BlockSyntax Body,
    bool IsSealed,
    bool IsFixed,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

/// <param name="SuccessPredicate">
/// The <c>where (…)</c> success contract, where <c>_</c> is the native return
/// value. Same keyword, placeholder and meaning as a refinement type — a native
/// call that fails its predicate throws a <c>NativeError</c> carrying errno.
/// </param>
/// <param name="NativeTarget">
/// The library named by a <c>from "lib"</c> clause on a standalone
/// <c>raw func</c>. Null inside a <c>bind</c> block, where the block already
/// names the library once for every function in it.
/// </param>
public sealed record NativeFunctionBindingSyntax(
    string Name,
    string SymbolName,
    IReadOnlyList<NativeFunctionParameterSyntax> Parameters,
    string? ReturnTypeName,
    string? CallingConventionName,
    TextSpan Span,
    ArgumentSyntax? SuccessPredicate = null,
    string? NativeTarget = null);

/// <summary>
/// A top-level <c>raw func name(…) -&gt; ret from "lib"</c>. Unlike a bind block,
/// which groups its functions under a module, this declares the name directly in
/// the enclosing scope — the point of the one-off form is that you call it by the
/// name you gave it.
/// </summary>
public sealed record RawFunctionStatementSyntax(
    NativeFunctionBindingSyntax Binding,
    DeclarationModifier Modifier,
    TextSpan Span) : StatementSyntax(Span);

public sealed record BindStatementSyntax(
    string ModuleName,
    string? NativeTarget,
    IReadOnlyList<NativeFunctionBindingSyntax> Functions,
    TextSpan Span) : StatementSyntax(Span);

public abstract record ClassMemberSyntax(bool IsShy, bool IsStatic, TextSpan Span);

public sealed record ClassPropertyMemberSyntax(
    string Name,
    string? TypeName,
    PipelineSyntax? Initializer,
    BlockSyntax? GetterBody,
    BlockSyntax? SetterBody,
    bool IsShy,
    bool IsStatic,
    bool IsFixed,
    bool IsVital,
    bool IsGuarded,
    bool IsLazy,
    bool IsFading,
    bool IsLocal,
    bool IsAbstract,
    TextSpan Span,
    DocComment? DocComment = null,
    ArgumentSyntax? Refinement = null) : ClassMemberSyntax(IsShy, IsStatic, Span);

/// <summary>
/// A <c>bind native "lib" { … }</c> block declared inside a class body, so the
/// library path is written once and the bound functions hang off the type that
/// wraps them rather than a separate module.
/// </summary>
/// <param name="IsShy">
/// Native members default to <c>shy</c> — the opposite of <c>func</c>. That is
/// the point of the design: the raw ABI surface stays hidden and a typed
/// <c>proud</c>/<c>shared prop</c> surface is written over it. <c>proud</c>
/// opts back out.
/// </param>
public sealed record ClassBindMemberSyntax(
    BindStatementSyntax Bind,
    bool IsShy,
    TextSpan Span) : ClassMemberSyntax(IsShy, IsStatic: true, Span);

public sealed record ClassMethodMemberSyntax(
    FunctionDefinitionStatementSyntax Method,
    bool IsStatic,
    bool IsShy,
    bool IsAbstract,
    bool IsOverride,
    bool IsGuarded,
    bool IsFading,
    bool IsLocal,
    bool IsRaw,
    TextSpan Span) : ClassMemberSyntax(IsShy, IsStatic, Span);

public sealed record ClassConstructorMemberSyntax(
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    BlockSyntax Body,
    TextSpan Span) : ClassMemberSyntax(IsShy: false, IsStatic: false, Span);

/// <summary>
/// A type declared inside a class body — <c>enum</c>, <c>class</c>, <c>struct</c>, <c>record</c>,
/// <c>union</c>, <c>interface</c> or <c>trait</c>.
/// </summary>
/// <remarks>
/// The declaration is carried verbatim as the statement it would be at the top level, so a nested
/// type is parsed by exactly the code that parses an outer one and cannot drift from it. What
/// nesting changes is where the type is registered, not how it is written.
/// </remarks>
public sealed record ClassNestedTypeMemberSyntax(
    StatementSyntax Declaration,
    string Name,
    bool IsShy,
    TextSpan Span) : ClassMemberSyntax(IsShy, IsStatic: true, Span);

/// <summary>An event member declared inside a class: <c>event OnName: PayloadType</c>.</summary>
public sealed record ClassEventMemberSyntax(
    string Name,
    string? PayloadTypeName,
    bool IsShy,
    TextSpan Span) : ClassMemberSyntax(IsShy, IsStatic: false, Span);

public sealed record ClassDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> PrimaryConstructorParameters,
    IReadOnlyList<ClassMemberSyntax> Members,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null,
    IReadOnlyList<string>? TypeParameters = null,
    string? BaseClassName = null,
    IReadOnlyList<PipelineSyntax>? BaseConstructorArgs = null,
    IReadOnlyList<string>? ImplementedInterfaces = null,
    IReadOnlyList<string>? UsedTraits = null,
    bool IsSealed = false,
    bool IsAbstract = false,
    bool IsHermit = false,
    bool IsStrict = false,
    bool IsPartial = false,
    IReadOnlyList<string>? BaseTypeArguments = null,
    IReadOnlyList<TypeParameterConstraintSyntax>? TypeParameterConstraints = null) : StatementSyntax(Span);

/// <summary>
/// Constraints on a generic type parameter, e.g. <c>where T: Numeric, Add</c>.
/// Multiple constraint clauses may apply to the same type parameter.
/// </summary>
public sealed record TypeParameterConstraintSyntax(
    string TypeParameter,
    IReadOnlyList<string> ConstraintNames,
    TextSpan Span);

public sealed record InterfaceMethodSignatureSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    string? ReturnTypeName,
    TextSpan Span);

public sealed record InterfaceDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<InterfaceMethodSignatureSyntax> Methods,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null,
    IReadOnlyList<string>? TypeParameters = null,
    IReadOnlyList<TypeParameterConstraintSyntax>? TypeParameterConstraints = null,
    IReadOnlyList<TypeParameterVariance>? TypeParameterVariances = null) : StatementSyntax(Span);

/// <summary>
/// Variance annotation on a generic type parameter declaration.
/// Currently meaningful only on interfaces (matches C# semantics):
/// <c>out T</c> declares <c>T</c> covariant (the type appears only in
/// output positions, allowing <c>IFoo&lt;Derived&gt;</c> to flow into a
/// <c>IFoo&lt;Base&gt;</c> slot); <c>in T</c> declares it contravariant
/// (input positions only, reversed flow); the default is invariant.
/// </summary>
public enum TypeParameterVariance
{
    Invariant,
    Covariant,
    Contravariant,
}

public sealed record UnionVariantSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> Fields,
    TextSpan Span);

public sealed record UnionDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<UnionVariantSyntax> Variants,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record ModuleDefinitionStatementSyntax(
    string Name,
    BlockSyntax Body,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null,
    bool IsPartial = false) : StatementSyntax(Span);

public sealed record EnumMemberSyntax(
    string Name,
    PipelineSyntax? Value,
    TextSpan Span);

public sealed record EnumDefinitionStatementSyntax(
    string Name,
    string? UnderlyingTypeName,
    IReadOnlyList<EnumMemberSyntax> Members,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record RecordFieldDefinitionSyntax(
    string Name,
    string? TypeName,
    PipelineSyntax? DefaultValue,
    bool IsOptional,
    TextSpan Span,
    ArgumentSyntax? Refinement = null);

public sealed record RecordDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<RecordFieldDefinitionSyntax> Fields,
    DeclarationModifier Modifier,
    bool IsSealed = false,
    bool IsStrict = false,
    bool IsPartial = false,
    TextSpan Span = default,
    DocComment? DocComment = null,
    IReadOnlyList<string>? TypeParameters = null,
    IReadOnlyList<TypeParameterConstraintSyntax>? TypeParameterConstraints = null) : StatementSyntax(Span);

public sealed record StructDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<RecordFieldDefinitionSyntax> Fields,
    IReadOnlyList<ClassMemberSyntax> Members,
    DeclarationModifier Modifier,
    bool IsSealed = false,
    bool IsFluid = false,
    bool IsPartial = false,
    TextSpan Span = default,
    DocComment? DocComment = null) : StatementSyntax(Span);

/// <summary>
/// One field line inside a <c>raw struct</c> body: <c>name: type[count] = default</c>.
/// </summary>
/// <param name="ArrayLength">
/// Element count for a fixed C array (<c>ulong[3]</c>) or inline char buffer
/// (<c>cstring[65]</c>). Null for a scalar field. The count is irreducible — it
/// is part of the ABI, and <c>char[65]</c> and <c>char[256]</c> are different
/// layouts — so it is always written out.
/// </param>
/// <param name="DefaultValue">
/// Applied when TōSh <em>constructs</em> a value, never by the marshaller: a CLR
/// struct cannot carry an initializer that <c>Marshal.PtrToStructure</c> would
/// run. So an <c>out</c> parameter still arrives zero-filled.
/// </param>
public sealed record RawStructFieldSyntax(
    string Name,
    string TypeName,
    int? ArrayLength,
    PipelineSyntax? DefaultValue,
    TextSpan Span,
    DocComment? DocComment = null);

/// <summary>
/// <c>raw struct Name [pack n] [size n] { field... }</c> — a C memory layout,
/// deliberately a separate declaration kind from TōSh <c>struct</c>.
///
/// TōSh structs are a dictionary-backed object model with computed properties,
/// methods and refinement-typed fields; a raw struct is a byte layout that
/// becomes a real sequential-layout CLR type. Merging them would force every
/// one of those features to answer "what does this mean in a layout?".
/// </summary>
/// <param name="DeclaredSize">
/// Optional assertion against <c>Marshal.SizeOf</c>, not a requirement. Padding
/// is never declared — <c>LayoutKind.Sequential</c> aligns naturally, exactly as
/// a C compiler does.
/// </param>
public sealed record RawStructDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<RawStructFieldSyntax> Fields,
    DeclarationModifier Modifier,
    bool IsUnion = false,
    int? Pack = null,
    int? DeclaredSize = null,
    TextSpan Span = default,
    DocComment? DocComment = null) : StatementSyntax(Span);

/// <summary>
/// A <c>raw callback Name(…) -&gt; ret</c> declaration: the C function-pointer
/// type a native signature names when it takes a callback.
///
/// Deliberately a named declaration rather than an inline structural type, for
/// the same reason <c>raw struct</c> is: it keeps
/// <see cref="NativeFunctionParameterSyntax.TypeName"/> a flat name that the
/// existing type registry resolves, and it matches how C actually spells these
/// — a <c>typedef</c>, near-universally.
/// </summary>
public sealed record RawCallbackDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<NativeFunctionParameterSyntax> Parameters,
    string? ReturnTypeName,
    string? CallingConventionName,
    DeclarationModifier Modifier,
    TextSpan Span = default,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record TraitMethodSignatureSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    string? ReturnTypeName,
    BlockSyntax? DefaultBody,
    TextSpan Span);

public sealed record TraitPropertySignatureSyntax(
    string Name,
    string? TypeName,
    PipelineSyntax? DefaultValue,
    TextSpan Span);

public sealed record TraitDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<TraitMethodSignatureSyntax> Methods,
    IReadOnlyList<TraitPropertySignatureSyntax> Properties,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record EventFieldDefinitionSyntax(
    string Name,
    string? TypeName,
    PipelineSyntax? DefaultValue,
    TextSpan Span);

public sealed record EventDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<EventFieldDefinitionSyntax> Fields,
    bool IsRequired,
    bool IsLocal,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record IfStatementSyntax(
    ArgumentSyntax Condition,
    BlockSyntax ThenBlock,
    BlockSyntax? ElseBlock,
    TextSpan Span) : StatementSyntax(Span);

public sealed record ForStatementSyntax(
    string VariableName,
    PipelineSyntax Source,
    BlockSyntax Body,
    TextSpan Span) : StatementSyntax(Span);

public sealed record WhileStatementSyntax(
    ArgumentSyntax Condition,
    BlockSyntax Body,
    TextSpan Span) : StatementSyntax(Span);

public sealed record UntilStatementSyntax(
    ArgumentSyntax Condition,
    BlockSyntax Body,
    TextSpan Span) : StatementSyntax(Span);

public sealed record ThrowStatementSyntax(PipelineSyntax? Value, TextSpan Span) : StatementSyntax(Span);

public sealed record CatchClauseSyntax(string? VariableName, BlockSyntax Body, TextSpan Span);

public sealed record TryStatementSyntax(
    BlockSyntax TryBlock,
    CatchClauseSyntax? CatchClause,
    BlockSyntax? FinallyBlock,
    TextSpan Span) : StatementSyntax(Span);

public sealed record DeferStatementSyntax(BlockSyntax Body, TextSpan Span) : StatementSyntax(Span);

public sealed record SwitchCaseSyntax(ArgumentSyntax MatchExpression, ArgumentSyntax? Guard, BlockSyntax Body, TextSpan Span);

public sealed record SwitchStatementSyntax(
    ArgumentSyntax Value,
    IReadOnlyList<SwitchCaseSyntax> Cases,
    BlockSyntax? DefaultBlock,
    TextSpan Span) : StatementSyntax(Span);

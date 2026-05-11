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

public sealed record DestructuringDeclarationStatementSyntax(DestructuringPatternSyntax Pattern, PipelineSyntax Value, DeclarationModifier Modifier, TextSpan Span) : StatementSyntax(Span);

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

public sealed record NativeFunctionBindingSyntax(
    string Name,
    string SymbolName,
    IReadOnlyList<NativeFunctionParameterSyntax> Parameters,
    string? ReturnTypeName,
    string? CallingConventionName,
    TextSpan Span);

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

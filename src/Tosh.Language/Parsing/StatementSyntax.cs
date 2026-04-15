using Tosh.Core;

namespace Tosh.Language.Parsing;

public abstract record StatementSyntax(TextSpan Span);

public enum DeclarationModifier
{
    Default,
    Shy,
    Global,
    Export,
}

public sealed record ScriptStatementSyntax(IReadOnlyList<StatementSyntax> Statements, TextSpan Span) : StatementSyntax(Span);

public sealed record PipelineStatementSyntax(PipelineSyntax Pipeline, TextSpan Span) : StatementSyntax(Span);

public sealed record VariableDeclarationStatementSyntax(string Name, string? TypeName, PipelineSyntax? Value, DeclarationModifier Modifier, TextSpan Span) : StatementSyntax(Span);

public abstract record DestructuringPatternSyntax(TextSpan Span);

public sealed record ArrayDestructuringPatternSyntax(IReadOnlyList<string> Names, TextSpan Span) : DestructuringPatternSyntax(Span);

public sealed record RecordDestructuringPatternSyntax(IReadOnlyList<string> Names, TextSpan Span) : DestructuringPatternSyntax(Span);

public sealed record DestructuringDeclarationStatementSyntax(DestructuringPatternSyntax Pattern, PipelineSyntax Value, DeclarationModifier Modifier, TextSpan Span) : StatementSyntax(Span);

public sealed record AllocStatementSyntax(string Name, PipelineSyntax Value, DeclarationModifier Modifier, TextSpan Span) : StatementSyntax(Span);

public sealed record VariableAssignmentStatementSyntax(string Name, string Operator, PipelineSyntax Value, TextSpan Span) : StatementSyntax(Span);

public sealed record MemberAssignmentStatementSyntax(ArgumentSyntax Target, string Operator, PipelineSyntax Value, TextSpan Span) : StatementSyntax(Span);

public sealed record ReturnStatementSyntax(PipelineSyntax? Value, TextSpan Span) : StatementSyntax(Span);

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

public sealed record RequireImportSyntax(string Name, string? Alias, TextSpan Span);

public sealed record RequireStatementSyntax(
    string Target,
    IReadOnlyList<RequireImportSyntax> Imports,
    bool IsNative,
    string? Alias,
    DeclarationModifier Modifier,
    TextSpan Span) : StatementSyntax(Span);

public sealed record FunctionParameterSyntax(string Name, string? TypeName, bool IsOptional, bool IsRest, TextSpan Span);

public enum NativeParameterPassingMode
{
    In,
    Ref,
    Out,
}

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
    TextSpan Span,
    DocComment? DocComment = null) : ClassMemberSyntax(IsShy, IsStatic: false, Span);

public sealed record ClassMethodMemberSyntax(
    FunctionDefinitionStatementSyntax Method,
    bool IsStatic,
    bool IsShy,
    TextSpan Span) : ClassMemberSyntax(IsShy, IsStatic, Span);

public sealed record ClassConstructorMemberSyntax(
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    BlockSyntax Body,
    TextSpan Span) : ClassMemberSyntax(IsShy: false, IsStatic: false, Span);

public sealed record ClassDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> PrimaryConstructorParameters,
    IReadOnlyList<ClassMemberSyntax> Members,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

public sealed record ModuleDefinitionStatementSyntax(
    string Name,
    BlockSyntax Body,
    DeclarationModifier Modifier,
    TextSpan Span,
    DocComment? DocComment = null) : StatementSyntax(Span);

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
    TextSpan Span);

public sealed record RecordDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<RecordFieldDefinitionSyntax> Fields,
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

public sealed record SwitchCaseSyntax(ArgumentSyntax MatchExpression, BlockSyntax Body, TextSpan Span);

public sealed record SwitchStatementSyntax(
    ArgumentSyntax Value,
    IReadOnlyList<SwitchCaseSyntax> Cases,
    BlockSyntax? DefaultBlock,
    TextSpan Span) : StatementSyntax(Span);

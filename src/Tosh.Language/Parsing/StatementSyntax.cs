using Tosh.Core;

namespace Tosh.Language.Parsing;

public abstract record StatementSyntax(TextSpan Span);

public sealed record ScriptStatementSyntax(IReadOnlyList<StatementSyntax> Statements, TextSpan Span) : StatementSyntax(Span);

public sealed record PipelineStatementSyntax(PipelineSyntax Pipeline, TextSpan Span) : StatementSyntax(Span);

public sealed record VariableDeclarationStatementSyntax(string Name, PipelineSyntax Value, TextSpan Span) : StatementSyntax(Span);

public sealed record VariableAssignmentStatementSyntax(string Name, PipelineSyntax Value, TextSpan Span) : StatementSyntax(Span);

public sealed record AliasStatementSyntax(string Name, PipelineSyntax Value, TextSpan Span) : StatementSyntax(Span);

public sealed record ReturnStatementSyntax(PipelineSyntax? Value, TextSpan Span) : StatementSyntax(Span);

public sealed record BreakStatementSyntax(TextSpan Span) : StatementSyntax(Span);

public sealed record ContinueStatementSyntax(TextSpan Span) : StatementSyntax(Span);

public sealed record UsingStatementSyntax(string Target, string? Alias, bool IsFileImport, TextSpan Span) : StatementSyntax(Span);

public sealed record FunctionParameterSyntax(string Name, string? TypeName, TextSpan Span);

public sealed record FunctionDefinitionStatementSyntax(
    string Name,
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    string? ReturnTypeName,
    BlockSyntax Body,
    TextSpan Span) : StatementSyntax(Span);

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

using Tosh.Core;

namespace Tosh.Language.Parsing;

public abstract record ArgumentSyntax(TextSpan Span);

public sealed record BarewordArgumentSyntax(string Value, TextSpan Span) : ArgumentSyntax(Span);

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

public sealed record FunctionReferenceArgumentSyntax(string Name, TextSpan Span) : ArgumentSyntax(Span);

public sealed record BlockArgumentSyntax(BlockSyntax Block, TextSpan Span) : ArgumentSyntax(Span);

public sealed record AnonymousFunctionArgumentSyntax(
    IReadOnlyList<FunctionParameterSyntax> Parameters,
    BlockSyntax Body,
    TextSpan Span) : ArgumentSyntax(Span);

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

public sealed record SubexpressionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record CommandSubstitutionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record InputProcessSubstitutionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record OutputProcessSubstitutionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record OperatorArgumentSyntax(
    ArgumentSyntax Left,
    string Operator,
    TextSpan OperatorSpan,
    ArgumentSyntax Right,
    TextSpan Span) : ArgumentSyntax(Span);

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
    TextSpan Span) : ArgumentSyntax(Span);

public abstract record InterpolatedStringPart;
public sealed record InterpolatedStringLiteralPart(string Text) : InterpolatedStringPart;
public sealed record InterpolatedStringExpressionPart(string Expression) : InterpolatedStringPart;

public sealed record InterpolatedStringArgumentSyntax(
    IReadOnlyList<InterpolatedStringPart> Parts,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record RangeArgumentSyntax(
    ArgumentSyntax Start,
    ArgumentSyntax? Step,
    ArgumentSyntax End,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record NameOfArgumentSyntax(string Identifier, bool IsVariableReference, TextSpan Span) : ArgumentSyntax(Span);

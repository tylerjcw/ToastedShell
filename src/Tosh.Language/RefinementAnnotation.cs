using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public abstract record RefinementClause(TextSpan Span);

public sealed record RefinementWhereClause(
    ArgumentSyntax Predicate,
    TextSpan Span) : RefinementClause(Span);

public sealed record RefinementCoerceClause(
    ArgumentSyntax? Guard,
    ArgumentSyntax Coercer,
    TextSpan Span) : RefinementClause(Span);

public sealed record RefinementAnnotation(
    IReadOnlyList<RefinementClause> Clauses,
    string SourceName,
    string SourceText,
    TextSpan Span,
    IReadOnlyList<LexicalScope>? CapturedScopes);

internal sealed record RefinementTypeDefinition(
    string Name,
    IReadOnlyList<string> TypeParameters,
    string BaseTypeName,
    RefinementAnnotation? Refinement,
    string SourceName,
    string SourceText,
    DeclarationModifier Modifier,
    TextSpan Span,
    string? Description = null) : IShellRefinementTypeDescriptor
{
    string IShellRefinementTypeDescriptor.Name => Name;
    string IShellRefinementTypeDescriptor.BaseTypeName => BaseTypeName;
    string? IShellRefinementTypeDescriptor.Description => Description;
}

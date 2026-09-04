using Tosh.Runtime;
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
    string? Description = null,

    /// <summary>
    /// The module this alias was declared in, or null at top level — <c>TOAST-0104</c>.
    /// </summary>
    /// <remarks>
    /// A base type is resolved where the alias is *used*, and by then the declaring module's
    /// scope has left the stack — so <c>export type Derived = Base where …</c> inside a module
    /// found nothing for <c>Base</c> and the whole alias silently ceased to exist. Carried for the
    /// same reason <c>AnnotationResolutionExports</c> exists for class members.
    /// </remarks>
    ModuleExportTable? DeclaringExports = null) : IShellRefinementTypeDescriptor
{
    string IShellRefinementTypeDescriptor.Name => Name;
    string IShellRefinementTypeDescriptor.BaseTypeName => BaseTypeName;
    string? IShellRefinementTypeDescriptor.Description => Description;
}

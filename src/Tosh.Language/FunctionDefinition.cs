using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed record FunctionDefinition(
    string Name,
    IReadOnlyList<FunctionParameterDefinition> Parameters,
    string? ReturnTypeName,
    BlockSyntax Body,
    bool IsCommandWrapper,
    string SourceName,
    string SourceText,
    TextSpan Span,
    IReadOnlyList<LexicalScope>? CapturedScopes = null,
    DocComment? DocComment = null,
    bool IsGenerator = false,
    IReadOnlyList<string>? TypeParameters = null,
    string? RawReturnTypeName = null,
    IReadOnlyList<ToshTypeParameterConstraint>? TypeParameterConstraints = null)
{
    private bool? _returnsExplicitly;

    /// <summary>
    /// Whether the body contains a <c>return</c> of its own — <c>TOSH-0010</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A return annotation describes the *returned value*. A function's body statements also
    /// contribute what they produce to its output, so a function that emits anything before
    /// returning was having its annotation checked against the emission as well — which is
    /// how `build.tosh`'s publish command, which writes `dotnet` output through an annotated
    /// function, ended every release reporting a conversion failure that had not happened.
    /// </para>
    /// <para>
    /// The distinction cannot be made when the value is emitted, because the `return` has not
    /// happened yet and the values stream — buffering them would cost the short-circuiting
    /// that lets `gen | first` stop an unbounded producer. So it is answered from the syntax,
    /// once, and cached.
    /// </para>
    /// </remarks>
    public bool ReturnsExplicitly => _returnsExplicitly ??= BlockReturns(Body);

    /// <summary>
    /// Statements that introduce a body of their own, whose <c>return</c> belongs to them.
    /// </summary>
    private static readonly HashSet<string> OwnScopeStatements = new(StringComparer.Ordinal)
    {
        nameof(FunctionDefinitionStatementSyntax),
        nameof(RuneDefinitionStatementSyntax),
        nameof(RawFunctionStatementSyntax),
        nameof(RawCallbackDefinitionStatementSyntax),
        nameof(ClassDefinitionStatementSyntax),
        nameof(RecordDefinitionStatementSyntax),
        nameof(StructDefinitionStatementSyntax),
        nameof(TraitDefinitionStatementSyntax),
        nameof(InterfaceDefinitionStatementSyntax),
        nameof(UnionDefinitionStatementSyntax),
        nameof(EnumDefinitionStatementSyntax),
        nameof(ModuleDefinitionStatementSyntax),
        nameof(ExtendStatementSyntax),
        nameof(SubcommandStatementSyntax),
    };

    /// <summary>Walks a block for a <c>return</c> that belongs to *this* function.</summary>
    /// <remarks>
    /// Reflective rather than a switch over the forty statement kinds, because a switch would
    /// silently miss the next block-carrying statement someone adds and answer "no return" for
    /// it. Arguments are not walked, so a lambda's <c>return</c> is naturally out of scope —
    /// it belongs to the lambda.
    /// </remarks>
    private static bool BlockReturns(BlockSyntax? block)
        => block is not null && block.Statements.Any(StatementReturns);

    private static bool StatementReturns(StatementSyntax? statement)
    {
        if (statement is null) return false;
        if (statement is ReturnStatementSyntax) return true;
        if (OwnScopeStatements.Contains(statement.GetType().Name)) return false;

        foreach (var property in statement.GetType().GetProperties())
        {
            switch (property.GetValue(statement))
            {
                case BlockSyntax nested when BlockReturns(nested):
                case StatementSyntax inner when StatementReturns(inner):
                    return true;

                case System.Collections.IEnumerable items and not string:
                    foreach (var item in items)
                    {
                        if (item is BlockSyntax b && BlockReturns(b)) return true;
                        if (item is StatementSyntax st && StatementReturns(st)) return true;
                    }

                    break;
            }
        }

        return false;
    }
}

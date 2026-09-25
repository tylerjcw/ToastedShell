using Tosh.Language.Binding;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The bindings lowering left dynamic without the source asking for it — a <c>var</c> or a
/// function parameter whose type could be neither inferred nor resolved.
/// </summary>
/// <remarks>
/// An explicit <c>dynamic</c>, <c>any</c> or <c>object</c> is a request, not a gap, and is not
/// reported. An annotation that failed to resolve is reported, because the written contract
/// was lost. Walks the statement shapes a binding can sit in: script, block, module, branch,
/// loop and function body.
/// </remarks>
internal static class DynamicFallbacks
{
    public static IReadOnlyList<string> In(ParseResult parse, ICommandTable commands)
    {
        var unit = Lowerer.Lower(parse, commands);
        var found = new List<string>();
        Collect(unit.Root, found);
        return found;
    }

    private static bool AskedForDynamic(string? typeName) =>
        typeName?.Trim() is { } name &&
        (string.Equals(name, "dynamic", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name, "any", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(name, "object", StringComparison.OrdinalIgnoreCase));

    private static void Collect(BoundNode node, List<string> found)
    {
        switch (node)
        {
            case BoundScript script:
                foreach (var statement in script.Statements) Collect(statement, found);
                break;
            case BoundBlock block:
                foreach (var statement in block.Statements) Collect(statement, found);
                break;
            case BoundModuleDefinition module:
                Collect(module.Body, found);
                break;
            case BoundIfStatement branch:
                Collect(branch.ThenBlock, found);
                if (branch.ElseBlock is not null) Collect(branch.ElseBlock, found);
                break;
            case BoundForStatement loop:
                Collect(loop.Body, found);
                break;
            case BoundWhileStatement loop:
                Collect(loop.Body, found);
                break;
            case BoundFunctionDefinition function:
                foreach (var parameter in function.Parameters)
                {
                    if (parameter.Symbol.DeclaredType.IsDynamic && !AskedForDynamic(parameter.TypeName))
                        found.Add($"parameter '{parameter.Name}' of '{function.Name}'");
                }
                Collect(function.Body, found);
                break;
            case BoundVariableDeclaration declaration
                when declaration.Symbol.DeclaredType.IsDynamic &&
                     !AskedForDynamic(declaration.Symbol.DeclaredTypeName):
                found.Add($"variable '{declaration.Symbol.Name}'");
                break;
        }
    }
}

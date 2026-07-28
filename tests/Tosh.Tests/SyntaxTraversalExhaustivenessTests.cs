using System.Collections;
using System.Reflection;
using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// TS-P2-07 — every syntax node that owns child nodes must be visited by
/// the traversals that walk the tree. Nothing enforced this: adding a
/// node meant remembering to extend each walker, and forgetting produced
/// no error, just a subtree that analysis silently skipped.
///
/// The check is mechanical. Reflection finds the node types and decides
/// which own children; a node with no child nodes is a leaf and needs no
/// traversal. The traversal source is then scanned for each type that
/// does. A new node type therefore fails this test until it is either
/// traversed or explicitly acknowledged.
/// </summary>
public sealed class SyntaxTraversalExhaustivenessTests
{
    /// <summary>
    /// Node types whose children are deliberately not walked, each with
    /// the reason. Anything not listed must be traversed.
    /// </summary>
    private static readonly Dictionary<string, string> AcknowledgedGaps = new(StringComparer.Ordinal)
    {
        ["ListComprehensionArgumentSyntax"] = "comprehension bodies are not walked for captures; must be extended when the compiler emits comprehensions",
        ["SetComprehensionArgumentSyntax"] = "as above",
        ["DictComprehensionArgumentSyntax"] = "as above",
        ["GeneratorComprehensionArgumentSyntax"] = "as above",
        ["RefinementClauseArgumentSyntax"] = "refinement clauses are evaluated by the engine's refinement path, not the variable binder",
        ["StaticMethodCallArgumentSyntax"] = "arguments resolve through the qualified-call path rather than variable binding",
        ["MemberProjectionArgumentSyntax"] = "projection targets are member names, not variable references",
    };

    private static IReadOnlyList<Type> NodeTypesOwningChildren()
    {
        var assembly = typeof(ArgumentSyntax).Assembly;
        return assembly
            .GetTypes()
            .Where(type => !type.IsAbstract
                && type.IsClass
                && typeof(ArgumentSyntax).IsAssignableFrom(type)
                && OwnsChildNodes(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// True when any public property carries another syntax node, either
    /// directly or as a collection element. That is what makes a node
    /// worth traversing.
    /// </summary>
    private static bool OwnsChildNodes(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsSyntaxNode(property.PropertyType))
            {
                return true;
            }

            if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType) &&
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericArguments().Any(IsSyntaxNode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSyntaxNode(Type type) =>
        typeof(ArgumentSyntax).IsAssignableFrom(type)
        || typeof(StatementSyntax).IsAssignableFrom(type)
        || type == typeof(PipelineSyntax)
        || type == typeof(BlockSyntax);

    private static string ReadTraversalSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tosh.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory!.FullName,
            "src",
            "Tosh.Language",
            "Binding",
            fileName);

        Assert.True(File.Exists(path), $"traversal source not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_variable_binder_visits_every_node_that_owns_children()
    {
        var source = ReadTraversalSource("VariableBinder.cs");

        var missing = NodeTypesOwningChildren()
            .Select(type => type.Name)
            .Where(name => !source.Contains(name, StringComparison.Ordinal))
            .Where(name => !AcknowledgedGaps.ContainsKey(name))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "VariableBinder does not visit these syntax nodes, so any variable "
            + "reference inside them is skipped by capture analysis. Either add "
            + "traversal, or record the node in AcknowledgedGaps with the reason: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void The_lowerer_is_total_by_construction()
    {
        // The lowerer deliberately does *not* need a case per node: it
        // ends in a fallback that wraps anything unrecognised in a
        // BoundDynamicExpression carrying the original syntax, so the
        // construct still reaches the engine and the compiler can report
        // it precisely. That fallback is the invariant worth protecting —
        // requiring a named case for every node would have been wrong,
        // and comprehensions travel exactly this route.
        var source = ReadTraversalSource("Lowerer.cs");

        Assert.Contains("BoundDynamicExpression", source, StringComparison.Ordinal);
        Assert.Contains("BoundDynamicStatement", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Acknowledged_gaps_still_exist_as_node_types()
    {
        // Keeps the allowlist honest: an entry that no longer names a real
        // node type is stale and should be removed rather than left to
        // silently excuse a future node with the same name.
        var known = typeof(ArgumentSyntax).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var stale = AcknowledgedGaps.Keys.Where(name => !known.Contains(name)).ToArray();

        Assert.True(
            stale.Length == 0,
            "these acknowledged gaps no longer name real syntax nodes: " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_acknowledged_gap_carries_a_reason()
    {
        Assert.All(
            AcknowledgedGaps,
            entry => Assert.False(
                string.IsNullOrWhiteSpace(entry.Value),
                $"{entry.Key} is excused without a reason"));
    }
}

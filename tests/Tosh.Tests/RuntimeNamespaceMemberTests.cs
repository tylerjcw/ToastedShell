using System.Reflection;

using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Everything a `$tosh` namespace answers to is also something it offers.
///
/// `TryGetMember` and `GetMembers` are two hand-written lists over the same properties, and
/// completion reads the second one. A member added to the first alone works when typed and
/// never appears when the reader is looking for it — which is worse than it not existing,
/// because nothing says it is there. `$tosh.Session.OpenHandles` was in that state, and so
/// were six properties added in one sitting.
/// </summary>
public class RuntimeNamespaceMemberTests
{
    /// <summary>Metadata rather than content: every record object carries it.</summary>
    private static readonly string[] NotOffered = ["ShellTypeName"];

    /// <summary>The live `$tosh`, which the engine populates when it first runs something.</summary>
    private static object Runtime()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        engine.ExecuteToListAsync("$tosh").GetAwaiter().GetResult();

        Assert.NotNull(runtime.RuntimeNamespace);
        return runtime.RuntimeNamespace!;
    }

    private static IEnumerable<object> Namespaces()
    {
        var root = Runtime();
        yield return root;

        foreach (var (_, value) in ((IShellRecordObject)root).GetMembers())
        {
            if (value is IShellRecordObject nested && nested.GetType().Name.StartsWith("Tosh", StringComparison.Ordinal))
            {
                yield return nested;
            }
        }
    }

    public static TheoryData<string> NamespaceNames()
    {
        var data = new TheoryData<string>();
        foreach (var value in Namespaces()) { data.Add(value.GetType().Name); }
        return data;
    }

    [Theory]
    [MemberData(nameof(NamespaceNames))]
    public void Every_property_a_namespace_has_is_one_it_offers(string typeName)
    {
        var target = Namespaces().First(n => n.GetType().Name == typeName);

        var properties = target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !NotOffered.Contains(name))
            .ToArray();

        var offered = ((IShellRecordObject)target)
            .GetMembers()
            .Select(m => m.Key)
            .ToHashSet(StringComparer.Ordinal);

        var missing = properties.Where(name => !offered.Contains(name)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"{typeName} has {string.Join(", ", missing)} but does not offer them, so they never complete.");
    }

    /// <summary>And everything offered can actually be fetched by name.</summary>
    [Theory]
    [MemberData(nameof(NamespaceNames))]
    public void Every_member_a_namespace_offers_can_be_fetched_by_name(string typeName)
    {
        var target = (IShellRecordObject)Namespaces().First(n => n.GetType().Name == typeName);

        foreach (var (name, _) in target.GetMembers())
        {
            Assert.True(target.TryGetMember(name, out _), $"{typeName} offers {name} but cannot resolve it.");
        }
    }

    /// <summary>The member that prompted this: it is reachable and it is offered.</summary>
    [Fact]
    public void The_terminal_namespace_is_both_reachable_and_offered()
    {
        var root = (IShellRecordObject)Runtime();

        Assert.True(root.TryGetMember("Terminal", out var value));
        Assert.IsType<TerminalProfile>(value);
        Assert.Contains(root.GetMembers(), m => m.Key == "Terminal");
    }
}

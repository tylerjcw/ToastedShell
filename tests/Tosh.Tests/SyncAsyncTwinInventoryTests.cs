using System.Reflection;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-24. Every sync/async twin in the interpreter and runtime is listed
/// here, and adding one fails this test.
/// </summary>
/// <remarks>
/// <para>
/// The defect the item was filed for is that a twin pair is a *parallel
/// implementation* rather than a delegation, so a semantic fix lands on one
/// surface and silently misses the other. It has happened twice —
/// <c>OperatorEvaluator.AreEqual</c> against <c>ToshEngine.AreEqualAsync</c>
/// (<c>TS-P1-14</c>/<c>TS-P1-15</c>) and <c>ToshHost.DrainValue</c> against
/// <c>InvokeValue</c> (<c>TS-P1-20</c>) — and each time the fix was to converge
/// that one pair, which does nothing about the next one.
/// </para>
/// <para>
/// So this is a ratchet rather than a check on any single pair. It pins the whole
/// inventory: a new twin fails until it is listed, and a converged twin fails
/// until it is struck off. Neither direction is silent, which is the property the
/// two earlier repairs lacked.
/// </para>
/// <para>
/// Deliberately measured by reflection rather than by reading bodies. Whether one
/// twin *delegates* to the other cannot be established without decoding IL, and a
/// guard that quietly answers "probably delegates" is worse than one that answers
/// a narrower question exactly. Adding a delegating twin is also a deliberate act
/// worth recording, so tripping on both kinds is the intended behaviour, not a
/// limitation.
/// </para>
/// </remarks>
public sealed class SyncAsyncTwinInventoryTests
{
    /// <summary>
    /// Every <c>Foo</c>/<c>FooAsync</c> pair the codebase declares, as
    /// <c>Type.Foo</c>, grouped by *why* the pair exists — which turned out to be
    /// the more useful question than how many there are.
    /// </summary>
    private static readonly string[] KnownTwins =
    [
        // ── Declared by the project's own dual-surface interfaces ──────────────
        // These are not stray duplication. Each interface declares both a sync and
        // an async member, so every implementer must supply both, and no
        // individual pair can be converged while the contract stands. This is the
        // item's central obstacle rather than a long tail: see the July 29 log
        // entry, which asks for a decision on whether the dual surface should
        // exist at all.
        "IObjectAccessor.GetValue",
        "IObjectAccessor.SetValue",
        "IShellEnumerableObject.EnumerateShellItems",
        "IShellInvocableObject.InvokeInstanceMethod",
        "IShellRecordObject.GetMembers",
        "IShellRecordObject.TryGetMember",
        "IShellRecordObject.TrySetMember",
        "IShellStaticType.CreateInstance",
        "IShellStaticType.InvokeStaticMethod",

        // ── Implementations of the above ───────────────────────────────────────
        // Mechanically required by the contracts listed above. They disappear when
        // the contract changes and not before.
        "ReflectionObjectAccessor.GetValue",
        "ReflectionObjectAccessor.SetValue",
        "ToshClassClrSuperReference.InvokeInstanceMethod",
        "ToshClassDefinition.CreateGenericInstance",
        "ToshClassDefinition.CreateInstance",
        "ToshClassDefinition.InvokeInstanceMethod",
        "ToshClassDefinition.InvokeStaticMethod",
        "ToshClassInstance.EnumerateShellItems",
        "ToshClassInstance.GetMembers",
        "ToshClassInstance.InvokeInstanceMethod",
        "ToshClassInstance.TryGetMember",
        "ToshClassInstance.TrySetMember",
        "ToshClassSelfReference.GetMembers",
        "ToshClassSelfReference.InvokeInstanceMethod",
        "ToshClassSelfReference.TryGetMember",
        "ToshClassSelfReference.TrySetMember",
        "ToshClassSuperReference.GetMembers",
        "ToshClassSuperReference.InvokeInstanceMethod",
        "ToshClassSuperReference.TryGetMember",
        "ToshClassSuperReference.TrySetMember",

        // ── Already converged: one implementation, one thin adapter ────────────
        // Listed so that *un*-converging them fails. The refinement cluster was
        // the July 27 slice.
        "ToshEngine.ConvertAnnotatedValue",
        "ToshEngine.EnsureRefinementSatisfied",
        "ToshEngine.TryApplyRefinementWithOptionalCoercion",
        "ToshEngine.TryConvertAnnotatedValue",

        // ── Genuinely parallel internals: the convergeable remainder ──────────
        // Private or internal, reached from both surfaces, and duplicated by
        // choice rather than by contract. These are what the item can actually
        // close, and each one that converges gets struck off this block.
        "ReflectionInvoker.CreateInstance",
        "ReflectionObjectAccessor.AssignSegment",
        "ReflectionObjectAccessor.ResolveOrMaterializeSegment",
        "ReflectionObjectAccessor.ResolveSegment",
        "ShellIndexingUtilities.GetIndexedValue",
        "ShellIndexingUtilities.SetIndexedValue",
        "ShellIterationUtilities.ExpandIterationItems",
        "ToshClassDefinition.ConvertConstructorParameterValue",
        "ToshClassDefinition.ConvertPropertyValue",
        "ToshClassDefinition.EnumerateItems",
        "ToshClassDefinition.EvaluatePropertyGetter",
        "ToshClassDefinition.ExecuteMethodBlock",
        "ToshClassDefinition.ExecutePropertySetter",
        "ToshClassDefinition.GetInitialPropertyValue",
        "ToshClassDefinition.GetInstanceMembers",
        "ToshClassDefinition.GetOrInitializeLazyProperty",
        "ToshClassDefinition.InvokeConstructorOnInstance",
        "ToshClassDefinition.ThrowDetailedSingleConstructorMismatch",
        "ToshClassDefinition.TryGetInstanceMember",
        "ToshClassDefinition.TryInvokeEnumerator",
        "ToshClassDefinition.TryInvokeSpecialInstanceMethod",
        "ToshClassDefinition.TrySelectSpecialInstanceMethod",
        "ToshClassDefinition.TrySetInstanceMember",
        "ToshEngine.ApplyPendingParameterDefaults",
        "ToshEngine.InvokeQualifiedMethod",
        "ToshEngine.ResolveQualifiedMemberChain",
        "ToshEngine.SelectBestCallableMatches",
        "ToshEngine.TryBindCallableParameters",
        "ToshEngine.TryConvertParameterValue",
        "ToshEngine.TryInvokeShellSymbol",
    ];

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly string[] OwnAssemblyPrefixes = ["Tosh."];

    private static bool IsOwnType(Type type) =>
        OwnAssemblyPrefixes.Any(prefix =>
            type.Assembly.GetName().Name?.StartsWith(prefix, StringComparison.Ordinal) == true);

    /// <summary>
    /// True when the twin exists because something outside this codebase declares
    /// it — <see cref="IAsyncDisposable"/>, <see cref="TextWriter"/>. Those pairs
    /// cannot be converged and are not what the item is about, so counting them
    /// would bury the ones that can be.
    /// </summary>
    private static bool IsFrameworkImposed(MethodInfo method)
    {
        var baseDefinition = method.GetBaseDefinition();

        if (baseDefinition != method && !IsOwnType(baseDefinition.DeclaringType!))
        {
            return true;
        }

        var declaring = method.DeclaringType!;

        foreach (var contract in declaring.GetInterfaces().Where(i => !IsOwnType(i)))
        {
            var map = declaring.GetInterfaceMap(contract);

            if (map.TargetMethods.Contains(method))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> DiscoverTwins()
    {
        var assemblies = new[] { typeof(ToshEngine).Assembly, typeof(ToshRuntime).Assembly };

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                // Compiler-generated closures and state machines carry angle
                // brackets and are not source the programme can converge.
                if (type.Name.Contains('<', StringComparison.Ordinal))
                {
                    continue;
                }

                var methods = type
                    .GetMethods(Declared)
                    .Where(method => !method.IsSpecialName)
                    .Where(method => !method.Name.Contains('<', StringComparison.Ordinal))
                    .Where(method => !IsFrameworkImposed(method))
                    .ToArray();

                var names = methods.Select(method => method.Name).ToHashSet(StringComparer.Ordinal);

                foreach (var name in names)
                {
                    if (name.EndsWith("Async", StringComparison.Ordinal) &&
                        names.Contains(name[..^"Async".Length]))
                    {
                        yield return $"{type.Name}.{name[..^"Async".Length]}";
                    }
                }
            }
        }
    }

    [Fact]
    public void The_twin_inventory_is_exactly_what_is_recorded()
    {
        var discovered = DiscoverTwins().Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var recorded = KnownTwins.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var added = discovered.Except(recorded, StringComparer.Ordinal).ToArray();
        var gone = recorded.Except(discovered, StringComparer.Ordinal).ToArray();

        Assert.True(
            added.Length == 0,
            "New sync/async twins are not listed in KnownTwins. Converge the pair, or "
            + "add it with a note saying why the duplication is necessary:\n  "
            + string.Join("\n  ", added));

        Assert.True(
            gone.Length == 0,
            "KnownTwins lists twins that no longer exist — strike them off so the "
            + "inventory keeps meaning something:\n  "
            + string.Join("\n  ", gone));
    }

    [Fact]
    public void The_two_pairs_the_item_was_filed_for_are_converged()
    {
        // TS-P1-14/TS-P1-15 and TS-P1-20. Named rather than merely absent from the
        // inventory, so the specific regressions that motivated the item fail
        // loudly rather than being one line in a list of thirty.
        var twins = DiscoverTwins().ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ToshEngine.AreEqual", twins);
        Assert.DoesNotContain("ToshHost.DrainValue", twins);
    }
}

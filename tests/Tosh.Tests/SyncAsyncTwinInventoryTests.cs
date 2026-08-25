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
        // Deliberate, decided July 29, and therefore out of scope for TS-P1-24's
        // convergence clause. Each interface declares both a sync and an async
        // member because the interpreter serves both kinds of caller with
        // different member-dispatch semantics — GetIndexedValueAsync avoids
        // re-entering the synchronous record API on purpose. No implementer can
        // delegate while the contract stands, and the contract is meant to stand.
        // Retiring the sync surface behind one blocking bridge was considered and
        // rejected as a larger change than this item; it would get its own item.
        "IObjectAccessor.GetValue",
        "IObjectAccessor.SetValue",
        // `TOAST-0006`: the synchronous compiler/runtime boundaries and asynchronous
        // evaluator share this contract. ReflectionInvoker's async form delegates to the
        // sync overload-selection core; another host may have a real async constructor.
        "IObjectInvoker.CreateInstance",
        "IShellEnumerableObject.EnumerateShellItems",
        "IShellInvocableObject.InvokeInstanceMethod",
        "IShellRecordObject.GetMembers",
        "IShellRecordObject.TryGetMember",
        "IShellRecordObject.TrySetMember",
        "IShellStaticType.CreateInstance",
        "IShellStaticType.InvokeStaticMethod",

        // ── IToastStream, `TOAST-0015` ─────────────────────────────────────────
        // A write destination is a file, a pipe, a buffer or a terminal, and the cost of
        // each differs by orders of magnitude. A buffer's async write is pure overhead; a
        // file's sync write blocks a pipeline stage. Both callers exist — redirection
        // writes asynchronously per value, and a display profile formats synchronously —
        // so the pair is the contract rather than an accident.
        //
        // The duplication is bounded: the async members carry default implementations that
        // delegate to the sync ones, so an implementer writes three methods and overrides
        // async only where it can do better. `ManagedFileHandle` takes the default;
        // `TextWriterStream` overrides, because a TextWriter's async path is real.
        "IToastStream.Flush",
        "IToastStream.WriteText",
        "IToastStream.WriteTextLine",
        "TextWriterStream.Flush",
        "TextWriterStream.WriteText",
        "TextWriterStream.WriteTextLine",
        "CompositeStream.Flush",
        "CompositeStream.WriteTextLine",

        // ── Implementations of the above ───────────────────────────────────────
        // Mechanically required by the contracts listed above, which are staying.
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

        // The July 30 slices. Each pair now shares one implementation of the
        // decisions it used to duplicate, with the twins reduced to the call that
        // genuinely differs:
        //   ConvertPropertyValue      -> TryResolvePropertyValueByBinding
        //   EvaluateClassPipelineValue-> BuildClassPipelineBlock + ProjectClassPipelineValues
        //   TryBindCallableParameters -> PlanCallableParameterBinding + ApplyMissingArgumentStep
        "ToshClassDefinition.ConvertPropertyValue",
        "ToshEngine.EvaluateClassPipelineValue",
        "ToshEngine.TryBindCallableParameters",
        //   TryGetInstanceMember      -> ResolveInstanceMemberRoute + TryGetClrBaseMember
        //   TrySetInstanceMember      -> ResolveInstanceMemberAssignment + TrySetClrBaseMember
        // both over one TryGetVisibleInstanceProperty, which the visibility rule
        // had been written out four times for.
        "ToshClassDefinition.TryGetInstanceMember",
        "ToshClassDefinition.TrySetInstanceMember",
        //   TrySelectSpecialInstanceMethod -> GetSpecialInstanceMethodCandidates
        //                                     + AmbiguousSpecialMethod
        //   TryInvokeEnumerator            -> EnumeratorMethodNames, which HasEnumerator
        //                                     had been spelling out a third time
        "ToshClassDefinition.TrySelectSpecialInstanceMethod",
        "ToshClassDefinition.TryInvokeEnumerator",
        //   InvokeQualifiedMethod      -> PlanQualifiedInvocation
        //   TryInvokeShellSymbol       -> TryPlanShellSymbol
        //   ResolveQualifiedMemberChain-> RequireMemberPath
        // The synchronous side of this trio is not an interpreter path: it is what
        // compiled ToastScript's host bridge calls, so a divergence here would mean
        // compiled and interpreted programs resolving the same dotted call
        // differently. Parity is asserted through InvokeQualifiedMethodPublic.
        "ToshEngine.InvokeQualifiedMethod",
        "ToshEngine.ResolveQualifiedMemberChain",
        "ToshEngine.TryInvokeShellSymbol",
        //   ApplyPendingParameterDefaults -> NeedsPendingDefault
        //   SelectBestCallableMatches     -> AccumulateBestMatch
        //   TryConvertParameterValue      -> DescribeAnnotationFailure
        //   GetInstanceMembers            -> IsVisibleInstanceProperty, which also
        //                                    fixed a real disagreement: the listing
        //                                    used a weaker rule than lookup, so it
        //                                    advertised `local` and `shared` members
        //                                    that member access then refused.
        "ToshClassDefinition.GetInstanceMembers",
        "ToshEngine.ApplyPendingParameterDefaults",
        "ToshEngine.SelectBestCallableMatches",
        "ToshEngine.TryConvertParameterValue",
        //   GetIndexedValue -> TryGetIndexedValueBeforeRecords +
        //                      GetIndexedValueAfterRecords, with each surface
        //                      supplying only its own record step between them.
        "ShellIndexingUtilities.GetIndexedValue",

        // ── Thin wrappers over one shared implementation ──────────────────────
        // Reclassified 2026-07-30 after measuring rather than counting. Each of
        // these bottoms out on the *same* async body: ExecuteClassBlockSync does
        // `ExecuteBlockAsync(…).GetAwaiter().GetResult()`, and the rest reach it
        // through that or block on their own async twin directly. Their sync
        // forms are 3-5 lines of call and cannot carry a semantic decision, so
        // they are not what TS-P1-24 is about — listing them beside genuinely
        // duplicated logic overstated the remaining work and, worse, made the
        // dangerous entries harder to see.
        //
        // Still listed, because adding another wrapper is a deliberate act.
        "ToshClassDefinition.EvaluatePropertyGetter",
        "ToshClassDefinition.ExecuteMethodBlock",
        "ToshClassDefinition.ExecutePropertySetter",
        "ToshClassDefinition.GetInitialPropertyValue",
        "ToshClassDefinition.GetOrInitializeLazyProperty",
        "ToshEngine.ExecuteClassBlock",

        // Verified 2026-08-01 rather than assumed:
        //   ReflectionInvoker.CreateInstance          — the async form is
        //     `ValueTask.FromResult(CreateInstance(…))`, already a delegation.
        //   TryInvokeSpecialInstanceMethod            — a six-line dispatcher over
        //     TrySelectSpecialInstanceMethod and ExecuteMethodBlock, both converged.
        //   EnumerateItems                            — IEnumerable/IAsyncEnumerable
        //     iterator bodies, which cannot share a body without materializing the
        //     sequence; changing streaming to remove eight lines is a bad trade.
        "ReflectionInvoker.CreateInstance",
        "ToshClassDefinition.EnumerateItems",
        "ToshClassDefinition.TryInvokeSpecialInstanceMethod",

        // ── Async prefix over a shared core ───────────────────────────────────
        // Reclassified 2026-07-30. Each async form handles the one genuinely
        // asynchronous case — an IShellRecordObject / IShellEnumerableObject
        // member call, which the project's dual-surface contracts require — and
        // then delegates to the synchronous core, passing includeShellRecord:false
        // so the record branch is not attempted twice. That is the correct shape
        // for a contract-imposed pair, not a parallel implementation: the decisions
        // live in one body and the prefix only chooses how to reach the member.
        //
        // A genuinely duplicated *message* did hide in this cluster and was
        // removed: the "$env is read-only" guidance was written out once per
        // surface, identical down to the suggested `export` form. Whoever improves
        // that wording would have improved one copy. The materialize-if-null
        // decision in ResolveOrMaterializeSegment is still mirrored, because the
        // store differs (TrySetValue versus TrySetMemberAsync) and only the
        // ExpandoObject construction is common — one line, not worth a seam.
        "ReflectionObjectAccessor.AssignSegment",
        "ReflectionObjectAccessor.ResolveOrMaterializeSegment",
        "ReflectionObjectAccessor.ResolveSegment",
        "ShellIterationUtilities.ExpandIterationItems",

        // ── Genuinely parallel internals: none remain ─────────────────────────
        // Twenty-nine at the start of this programme, zero now: some converged onto
        // a shared decision, some deleted as unreachable, some reclassified after
        // reading them. The block is kept so that adding one lands here visibly.
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
                    if (!name.EndsWith("Async", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var stem = name[..^"Async".Length];

                    // Both spellings of the convention. Looking only for `Foo`/`FooAsync` was a
                    // blind spot in this guard's own discovery rule: `ExecuteClassBlockSync` and
                    // `EvaluateClassPipelineValueSync` are twins by any reading, and neither was
                    // ever counted. The second was byte-identical to its async form apart from
                    // the block call — precisely what this inventory exists to surface.
                    if (names.Contains(stem) || names.Contains(stem + "Sync"))
                    {
                        yield return $"{type.Name}.{stem}";
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

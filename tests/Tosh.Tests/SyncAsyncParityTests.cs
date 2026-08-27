using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The synchronous and asynchronous member surfaces answer identically — <c>TS-P1-24</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>SyncAsyncTwinInventoryTests</c> guards against *new* twins appearing. It cannot say whether
/// the twins that exist agree, which is the property that actually matters: this item was filed
/// because a semantic fix landed on one surface and silently missed the other, twice
/// (<c>TS-P1-14</c>/<c>TS-P1-15</c>, and <c>TS-P1-20</c>).
/// </para>
/// <para>
/// The target is <c>TrySetMember</c> / <c>TrySetMemberAsync</c>, which reach
/// <c>ToshClassDefinition.TrySetInstanceMember</c> and its twin — two of the genuinely parallel
/// live internals, and the pair that runs the property-conversion decision this slice converged.
/// </para>
/// <para>
/// <b>Construction is deliberately not tested here.</b> A first draft asserted parity over
/// <c>CreateInstance</c> / <c>CreateInstanceAsync</c> and would have passed no matter what:
/// <c>CreateInstance</c> is a four-line wrapper that blocks on <c>CreateInstanceAsync</c>, so the
/// test compared one implementation with itself. A parity test over a delegating pair proves only
/// that delegation works.
/// </para>
/// </remarks>
public sealed class SyncAsyncParityTests
{
    private static async Task<ToshClassInstance> ConstructAsync(string source, string className)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(source);

        Assert.True(engine.TryGetNamedType(className, out var type), $"'{className}' was not declared");
        var definition = Assert.IsType<ToshClassDefinition>(type);

        return Assert.IsType<ToshClassInstance>(
            await definition.CreateInstanceAsync([], CancellationToken.None));
    }

    /// <summary>Sets a member through both surfaces, returning what each did.</summary>
    private static async Task<(string Sync, string Async)> SetBothWaysAsync(
        string source,
        string className,
        string member,
        object? value)
    {
        // Separate instances: setting through one surface would otherwise change what the other
        // sees, and a shared instance can make disagreeing paths look like they agree.
        var syncInstance = await ConstructAsync(source, className);
        var asyncInstance = await ConstructAsync(source, className);

        return (Describe(() => syncInstance.TrySetMember(member, value)),
                await DescribeAsync(async () =>
                    await asyncInstance.TrySetMemberAsync(member, value, CancellationToken.None)));
    }

    private static string Describe(Func<bool> action)
    {
        try
        {
            return $"ok:{action()}";
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static async Task<string> DescribeAsync(Func<Task<bool>> action)
    {
        try
        {
            return $"ok:{await action()}";
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}:{exception.Message}";
        }
    }

    private const string TypedProperty =
        """
        class Typed {
            prop N: int = 0
        }
        """;

    [Fact]
    public async Task A_valid_assignment_agrees_on_both_surfaces()
    {
        var (sync, async) = await SetBothWaysAsync(TypedProperty, "Typed", "N", 7);

        Assert.Equal("ok:True", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_failed_conversion_agrees_on_both_surfaces()
    {
        // The converged decision: which diagnostic a bad value earns for an annotated property.
        var (sync, async) = await SetBothWaysAsync(TypedProperty, "Typed", "N", "not-a-number");

        Assert.StartsWith("Tosh", sync, StringComparison.Ordinal);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_failed_refinement_agrees_on_both_surfaces()
    {
        // The branch that passes an already-precise diagnostic through untouched rather than
        // rewording it — a rule that used to be written out twice, once per surface.
        var (sync, async) = await SetBothWaysAsync(
            """
            type Small = int where (_ >= 0 and _ <= 10)
            class Holder {
                prop N: Small = 0
            }
            """,
            "Holder",
            "N",
            99);

        Assert.Contains("refinement", sync, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task An_unknown_member_agrees_on_both_surfaces()
    {
        var (sync, async) = await SetBothWaysAsync(TypedProperty, "Typed", "NoSuchMember", 1);

        Assert.Equal("ok:False", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_read_after_each_write_agrees_on_both_surfaces()
    {
        // Parity of the *effect*, not just of the return value: a surface could report success
        // and store something different.
        var source = TypedProperty;
        var syncInstance = await ConstructAsync(source, "Typed");
        var asyncInstance = await ConstructAsync(source, "Typed");

        Assert.True(syncInstance.TrySetMember("N", 21));
        Assert.True(await asyncInstance.TrySetMemberAsync("N", 21, CancellationToken.None));

        Assert.True(syncInstance.TryGetMember("N", out var syncValue));
        Assert.True(asyncInstance.TryGetMember("N", out var asyncValue));

        Assert.Equal(21, syncValue);
        Assert.Equal(syncValue, asyncValue);
    }

    // ── Overload binding, driven directly on both surfaces ─────────────────────

    /// <summary>
    /// Binds <paramref name="arguments"/> against a signature through both binders and returns
    /// each one's full outcome, rendered so any difference shows up in the assertion message.
    /// </summary>
    /// <remarks>
    /// These are reachable because <c>Tosh.Language</c> grants <c>InternalsVisibleTo</c> to this
    /// project. Driving them directly matters: overload *scoring* decides which candidate wins,
    /// and a score that differed between the surfaces would pick different overloads for the same
    /// call without either binder throwing — a silent wrong answer rather than a failure.
    /// </remarks>
    private static async Task<(string Sync, string Async)> BindBothWaysAsync(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        params object?[] arguments)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        string sync;
        try
        {
            var ok = engine.TryBindCallableParameters(
                parameters, arguments, out var locals, out var score, out var pending);
            sync = Render(ok, locals, score, pending);
        }
        catch (Exception exception)
        {
            sync = $"{exception.GetType().Name}:{exception.Message}";
        }

        string async;
        try
        {
            var result = await engine.TryBindCallableParametersAsync(
                parameters, arguments, CancellationToken.None);
            async = Render(result.Success, result.Locals, result.Score, result.PendingDefaults);
        }
        catch (Exception exception)
        {
            async = $"{exception.GetType().Name}:{exception.Message}";
        }

        return (sync, async);
    }

    private static string Render(
        bool success,
        Dictionary<string, object?> locals,
        int score,
        List<FunctionParameterDefinition>? pendingDefaults)
    {
        var bound = string.Join(
            ",",
            locals.Where(entry => entry.Key != "args")
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={Format(entry.Value)}"));

        var pending = pendingDefaults is null
            ? "-"
            : string.Join("|", pendingDefaults.Select(parameter => parameter.Name));

        return $"success={success} score={score} bound=[{bound}] pending={pending}";
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        List<object?> list => $"[{string.Join(" ", list.Select(Format))}]",
        object[] array => $"[{string.Join(" ", array.Select(Format))}]",
        _ => value.ToString() ?? "null",
    };

    private static FunctionParameterDefinition Parameter(
        string name,
        string? typeName = null,
        bool isOptional = false,
        bool isRest = false) =>
        new(name, typeName, isOptional, isRest, DefaultValue: null, Span: default);

    [Fact]
    public async Task Positional_binding_agrees_on_both_surfaces()
    {
        var (sync, async) = await BindBothWaysAsync([Parameter("a"), Parameter("b")], 1, 2);

        Assert.Equal("success=True score=0 bound=[a=1,b=2] pending=-", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_missing_argument_scores_identically_on_both_surfaces()
    {
        // Scoring is the part that silently picks a different overload if it drifts, so the
        // assertion pins the number rather than merely comparing the two surfaces: if both
        // drifted together, comparing them alone would still pass.
        //
        // `b` is optional because a *required* parameter with no argument fails binding outright
        // rather than scoring — an earlier draft asserted score=4 against two required parameters
        // and failed, which is the binder being stricter than the test assumed.
        var (sync, async) = await BindBothWaysAsync(
            [Parameter("a"), Parameter("b", isOptional: true)],
            1);

        Assert.Equal("success=True score=4 bound=[a=1,b=null] pending=-", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_named_argument_agrees_on_both_surfaces()
    {
        var (sync, async) = await BindBothWaysAsync(
            [Parameter("a"), Parameter("b")],
            new NamedArgument("b", 9),
            1);

        Assert.Equal("success=True score=0 bound=[a=1,b=9] pending=-", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task An_unmatched_named_argument_is_rejected_on_both_surfaces()
    {
        var (sync, async) = await BindBothWaysAsync(
            [Parameter("a")],
            new NamedArgument("nosuch", 1));

        Assert.StartsWith("success=False", sync, StringComparison.Ordinal);
        Assert.Equal(sync, async);
    }

    [Theory]
    // Too many arguments for the signature, and too few for its required parameters.
    [InlineData(3)]
    [InlineData(0)]
    public async Task An_arity_mismatch_agrees_on_both_surfaces(int count)
    {
        var arguments = Enumerable.Range(1, count).Cast<object?>().ToArray();
        var (sync, async) = await BindBothWaysAsync([Parameter("a"), Parameter("b")], arguments);

        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_rest_parameter_collects_identically_on_both_surfaces()
    {
        var (sync, async) = await BindBothWaysAsync(
            [Parameter("first"), Parameter("rest", isRest: true)],
            1, 2, 3);

        Assert.Equal("success=True score=0 bound=[first=1,rest=[2 3]] pending=-", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task An_empty_rest_parameter_still_binds_on_both_surfaces()
    {
        // The edge the refactor could most easily have dropped: with no trailing arguments there
        // are no rest *steps* at all, yet the parameter must still bind — to an empty list, not
        // to nothing. Caught while rewriting; pinned so it stays caught.
        var (sync, async) = await BindBothWaysAsync(
            [Parameter("first"), Parameter("rest", isRest: true)],
            1);

        Assert.Equal("success=True score=0 bound=[first=1,rest=[]] pending=-", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_coerced_argument_scores_identically_on_both_surfaces()
    {
        // The coercion penalty: a converted argument scores worse than an exact match, which is
        // how an overload taking `int` loses to one taking `string` for a string argument.
        //
        // This case exists because a negative control found the gap. Diverging only the sync
        // binder's `score += 1` left every other assertion here green — none of them coerced
        // anything, so the penalty was never exercised and the parity suite could not have caught
        // a drift in the one number that decides overload resolution.
        var (sync, async) = await BindBothWaysAsync([Parameter("n", "int")], "42");

        Assert.Equal("success=True score=1 bound=[n=42] pending=-", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task An_exact_match_is_not_penalised_on_either_surface()
    {
        // The other half of the same rule: an argument that needs no conversion must score 0, or
        // the penalty above would be meaningless.
        var (sync, async) = await BindBothWaysAsync([Parameter("n", "int")], 42);

        Assert.Equal("success=True score=0 bound=[n=42] pending=-", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_failed_conversion_agrees_on_both_binders()
    {
        var (sync, async) = await BindBothWaysAsync([Parameter("n", "int")], "not-a-number");

        Assert.Equal(sync, async);
    }

    // ── Member reads, across every route the router can pick ───────────────────

    /// <summary>Reads a member through both surfaces, returning what each produced.</summary>
    private static async Task<(string Sync, string Async)> GetBothWaysAsync(
        string source,
        string className,
        string member)
    {
        var instance = await ConstructAsync(source, className);

        var sync = Describe(() =>
        {
            var found = instance.TryGetMember(member, out var value);
            return $"{found}:{value ?? "null"}";
        });

        var async = await DescribeAsync(async () =>
        {
            var result = await instance.TryGetMemberAsync(member, includeHidden: false, CancellationToken.None);
            return $"{result.Found}:{result.Value ?? "null"}";
        });

        return (sync, async);
    }

    private static string Describe(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}:{exception.Message}";
        }
    }

    private static async Task<string> DescribeAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}:{exception.Message}";
        }
    }

    private const string EveryRoute =
        """
        class Base {
            prop Inherited: int = 5
        }
        class Routes extends Base {
            prop Stored: int = 1
            prop Computed => 2
            lazy prop Lazy = 3
            shy prop Hidden: int = 4
            fixed prop Fixed: int = 6
        }
        """;

    [Theory]
    // One case per route the shared router can return, plus the two fall-throughs beneath it.
    [InlineData("Stored", "True:1")]
    [InlineData("Computed", "True:2")]
    [InlineData("Lazy", "True:3")]
    [InlineData("Inherited", "True:5")]     // resolved on the base class
    [InlineData("Hidden", "False:null")]    // shy, and includeHidden is false on both surfaces
    [InlineData("NoSuchMember", "False:null")]
    public async Task Every_read_route_agrees_on_both_surfaces(string member, string expected)
    {
        var (sync, async) = await GetBothWaysAsync(EveryRoute, "Routes", member);

        Assert.Equal(expected, sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task Assigning_a_computed_property_fails_the_same_way_on_both_surfaces()
    {
        // The read-only message, which used to be written out once per surface.
        var (sync, async) = await SetBothWaysAsync(EveryRoute, "Routes", "Computed", 9);

        Assert.Contains("read-only", sync, StringComparison.Ordinal);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task Reassigning_a_fixed_property_fails_the_same_way_on_both_surfaces()
    {
        // The `fixed` message, likewise — and the route that only triggers once the instance has
        // finished initializing, so it exercises the instance state the router consults.
        var (sync, async) = await SetBothWaysAsync(EveryRoute, "Routes", "Fixed", 9);

        Assert.Contains("fixed", sync, StringComparison.Ordinal);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task An_inherited_property_is_written_the_same_way_on_both_surfaces()
    {
        // The base-class fall-through on the write path, which recurses into the parent's own
        // twin rather than the shared router.
        var (sync, async) = await SetBothWaysAsync(EveryRoute, "Routes", "Inherited", 42);

        Assert.Equal("ok:True", sync);
        Assert.Equal(sync, async);
    }

    // ── Iteration, and the enumerator name list ────────────────────────────────

    private static async Task<(string Sync, string Async, bool HasEnumerator)> IterateBothWaysAsync(
        string source,
        string className)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(source);

        Assert.True(engine.TryGetNamedType(className, out var type));
        var definition = Assert.IsType<ToshClassDefinition>(type);
        var instance = Assert.IsType<ToshClassInstance>(
            await definition.CreateInstanceAsync([], CancellationToken.None));

        var sync = string.Join(",", definition.EnumerateItems(instance).Select(item => item?.ToString()));

        var asyncItems = new List<string?>();
        await foreach (var item in definition.EnumerateItemsAsync(instance, CancellationToken.None))
        {
            asyncItems.Add(item?.ToString());
        }

        return (sync, string.Join(",", asyncItems), definition.HasEnumerator);
    }

    [Theory]
    // Both recognised spellings, so the shared name list is exercised rather than assumed. It
    // previously existed in *three* places — the two dispatch paths and HasEnumerator — and a
    // fourth spelling added to only some of them would make a class iterable on one surface, or
    // report itself iterable and then not be.
    [InlineData("enumerate")]
    [InlineData("GetEnumerator")]
    public async Task An_iterable_class_agrees_on_both_surfaces(string methodName)
    {
        var (sync, async, hasEnumerator) = await IterateBothWaysAsync(
            $$"""
            class Seq {
                func {{methodName}}() { return [1, 2, 3] }
            }
            """,
            "Seq");

        Assert.Equal("1,2,3", sync);
        Assert.Equal(sync, async);
        Assert.True(hasEnumerator, "the class defines an enumerator but does not report one");
    }

    [Fact]
    public async Task A_class_without_an_enumerator_yields_itself_on_both_surfaces()
    {
        var (sync, async, hasEnumerator) = await IterateBothWaysAsync(
            """
            class Plain {
                prop N: int = 1
            }
            """,
            "Plain");

        Assert.False(hasEnumerator);
        Assert.Equal(sync, async);
        Assert.NotEmpty(sync);
    }

    [Fact]
    public async Task An_ambiguous_special_method_fails_the_same_way_on_both_surfaces()
    {
        // The ambiguity message, previously written out once per surface. Two same-arity
        // overloads of an enumerator tie, and both surfaces must describe the tie identically.
        var source =
            """
            class Tied {
                func enumerate(a: int) { return [1] }
                func enumerate(b: int) { return [2] }
            }
            """;

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(source);
        Assert.True(engine.TryGetNamedType("Tied", out var type));
        var definition = Assert.IsType<ToshClassDefinition>(type);
        var instance = Assert.IsType<ToshClassInstance>(
            await definition.CreateInstanceAsync([], CancellationToken.None));

        var sync = Describe(() =>
            $"{definition.TryInvokeSpecialInstanceMethod(instance, "enumerate", [1], out var value)}:{value}");

        var async = await DescribeAsync(async () =>
        {
            var result = await definition.TryInvokeSpecialInstanceMethodAsync(
                instance, "enumerate", [1], CancellationToken.None);
            return $"{result.Matched}:{result.Value}";
        });

        Assert.Contains("Multiple overloads matched special method", sync, StringComparison.Ordinal);
        Assert.Equal(sync, async);
    }

    // ── Qualified invocation: the interpreter/compiler seam ────────────────────

    /// <summary>
    /// Invokes a dotted path through both surfaces and returns what each produced.
    /// </summary>
    /// <remarks>
    /// This pair matters more than the others because the *synchronous* side is not an
    /// interpreter path at all: `InvokeQualifiedMethodPublic` exists so compiled ToastScript's
    /// host bridge can dispatch a `BoundStaticMethodCall` without re-parsing. So a divergence
    /// here means compiled and interpreted programs resolve the same dotted call differently —
    /// the same shape as <c>TS-P1-40</c>, reached from the other direction.
    /// </remarks>
    private static async Task<(string Sync, string Async)> InvokeQualifiedBothWaysAsync(
        string declarations,
        string path,
        params object?[] arguments)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        if (declarations.Length > 0)
        {
            await engine.ExecuteToListAsync(declarations);
        }

        // Compared by *message*, not by exception type. The asynchronous side is observed
        // through ExecuteToListAsync, which wraps a failure in a ToshDiagnosticException carrying
        // a source span, while the synchronous side is called directly and throws bare. That
        // difference is the engine adding context, not the two invocation paths disagreeing.
        //
        // It is a real difference for a *reader* though: compiled ToastScript, which is what the
        // synchronous side exists for, gets an InvalidOperationException with no span where the
        // interpreter gets a full diagnostic. Recorded on TS-P1-40 rather than fixed here, since
        // the compiler is in maintenance.
        string Message(Exception exception) => exception.Message;

        string sync;
        try
        {
            sync = $"{engine.InvokeQualifiedMethodPublic(path, arguments) ?? "null"}";
        }
        catch (Exception exception)
        {
            sync = Message(exception);
        }

        var rendered = string.Join(", ", arguments.Select(argument => argument?.ToString() ?? "null"));

        string async;
        try
        {
            var results = await engine.ExecuteToListAsync($"{path}({rendered})");
            async = $"{(results.Count == 0 ? "null" : results[^1]) ?? "null"}";
        }
        catch (Exception exception)
        {
            async = Message(exception);
        }

        return (sync, async);
    }

    [Fact]
    public async Task A_clr_static_call_agrees_on_both_surfaces()
    {
        var (sync, async) = await InvokeQualifiedBothWaysAsync("", "System.Math.Max", 3, 7);

        Assert.Equal("7", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_module_function_agrees_on_both_surfaces()
    {
        // The shell-symbol branch: a module name followed by an exported function.
        var (sync, async) = await InvokeQualifiedBothWaysAsync(
            """
            module Lib {
                export func twice(n) { return ($n * 2) }
            }
            """,
            "Lib.twice",
            21);

        Assert.Equal("42", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_nested_module_function_agrees_on_both_surfaces()
    {
        // More than two segments, so the middle of the path becomes a member walk rather than
        // part of the name — the segment arithmetic both twins used to do separately.
        var (sync, async) = await InvokeQualifiedBothWaysAsync(
            """
            module Outer {
                export module Inner {
                    export func greet() { return "hi" }
                }
            }
            """,
            "Outer.Inner.greet");

        Assert.Equal("hi", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_type_named_as_a_method_fails_the_same_way_on_both_surfaces()
    {
        // The "construct instances with 'new …'" rejection, which existed once per surface.
        var (sync, async) = await InvokeQualifiedBothWaysAsync("", "System.Text.StringBuilder");

        Assert.Contains("Construct instances with", sync, StringComparison.Ordinal);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task An_unresolvable_path_fails_the_same_way_on_both_surfaces()
    {
        var (sync, async) = await InvokeQualifiedBothWaysAsync("", "No.Such.Path.method");

        Assert.Contains("Unable to resolve", sync, StringComparison.Ordinal);
        Assert.Equal(sync, async);
    }

    // ── Indexing, the last of the parallel internals ───────────────────────────

    /// <summary>Indexes a target through both surfaces and returns what each produced.</summary>
    private static async Task<(string Sync, string Async)> IndexBothWaysAsync(
        object? target,
        object? index,
        IndexLookupKind lookupKind = IndexLookupKind.Default)
    {
        string sync;
        try
        {
            sync = $"{ShellIndexingUtilities.GetIndexedValue(target, index, lookupKind) ?? "null"}";
        }
        catch (Exception exception)
        {
            sync = exception.Message;
        }

        string async;
        try
        {
            var value = await ShellIndexingUtilities.GetIndexedValueAsync(
                target, index, lookupKind, CancellationToken.None);
            async = $"{value ?? "null"}";
        }
        catch (Exception exception)
        {
            async = exception.Message;
        }

        return (sync, async);
    }

    [Theory]
    // Every shape the shared pre-record half handles.
    [InlineData(new object[] { 10, 20, 30 }, 1, "20")]
    [InlineData(new object[] { 10, 20, 30 }, 0, "10")]
    public async Task An_integer_index_agrees_on_both_surfaces(object[] items, int index, string expected)
    {
        var (sync, async) = await IndexBothWaysAsync(items, index);

        Assert.Equal(expected, sync);
        Assert.Equal(sync, async);
    }

    [Theory]
    [InlineData("abc", 1, "b")]
    // Out of range, so the message itself has to match — it is the shared half that produces it.
    [InlineData("abc", 9, "Index 9 is out of range for string length 3.")]
    [InlineData("abc", -1, "Indexes must be zero or greater.")]
    public async Task A_string_index_agrees_on_both_surfaces(string text, int index, string expected)
    {
        var (sync, async) = await IndexBothWaysAsync(text, index);

        Assert.Equal(expected, sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_numeric_string_key_still_reaches_the_dictionary()
    {
        // The case that made this convergence delicate, and that an earlier attempt broke.
        // TryGetIntegerIndex accepts "3", so a numeric string enters the integer branch — which
        // sits ahead of record and dictionary lookup. That branch must *fall through* when the
        // target is none of the integer-indexable shapes, or a dictionary key spelled "3" stops
        // resolving. The first extraction ended it with a throw and turned this into an error;
        // caught by probing the shell, not by the suite, which is why it is pinned here now.
        var dictionary = new Dictionary<object, object?> { ["0"] = "zero", ["3"] = "THREE" };
        var (sync, async) = await IndexBothWaysAsync(dictionary, "3");

        Assert.Equal("THREE", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_numeric_string_index_still_means_the_element()
    {
        // The reason the split is before/after rather than an async prefix. TryGetIntegerIndex
        // accepts "3", so a numeric string reaches the integer branch — which sits *ahead* of
        // record access. Hoisting the record lookup to the front, the obvious convergence, would
        // silently change this to mean "the field named 3".
        var (sync, async) = await IndexBothWaysAsync(new object[] { "a", "b", "c", "d" }, "3");

        Assert.Equal("d", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_dictionary_key_agrees_on_both_surfaces()
    {
        var dictionary = new Dictionary<string, object?> { ["alpha"] = 1, ["beta"] = 2 };
        var (sync, async) = await IndexBothWaysAsync(dictionary, "beta");

        Assert.Equal("2", sync);
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task A_missing_key_fails_the_same_way_on_both_surfaces()
    {
        var dictionary = new Dictionary<string, object?> { ["alpha"] = 1 };
        var (sync, async) = await IndexBothWaysAsync(dictionary, "nope");

        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task Indexing_null_fails_the_same_way_on_both_surfaces()
    {
        var (sync, async) = await IndexBothWaysAsync(null, 0);

        Assert.Equal("Cannot index into null.", sync);
        Assert.Equal(sync, async);
    }
}

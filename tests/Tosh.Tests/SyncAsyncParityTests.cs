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
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
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
}

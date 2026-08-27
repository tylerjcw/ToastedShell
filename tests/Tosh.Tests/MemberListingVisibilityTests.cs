using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>members</c> on an instance advertises only what member access will serve — <c>TS-P2-47</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>$c | members</c> resolves the instance to its <i>type</i> descriptor, and that listing
/// applied its own visibility filter — <c>includeHidden || !property.IsShy</c> — weaker than the
/// rule member access uses. So it advertised <c>local</c> and <c>guarded</c> properties that
/// <c>$c.Local</c> then refused with "Member not found": the type promising what the instance
/// denies.
/// </para>
/// <para>
/// The descriptor now asks the same predicate the instance paths share, which is the point of
/// having converged that rule in the first place. A fourth copy of a visibility test is a fourth
/// chance for a modifier to be honoured in three places (<c>TS-P1-24</c>).
/// </para>
/// </remarks>
public sealed class MemberListingVisibilityTests
{
    private static async Task<string[]> ListedMembersAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(source);

        Assert.True(engine.TryGetNamedType("C", out var type));
        var definition = Assert.IsType<ToshClassDefinition>(type);

        return definition.GetShellMembers().Select(member => member.Name).ToArray();
    }

    private const string Modifiers =
        """
        class C {
            prop Open = 1
            local prop Local = 2
            guarded prop Guard = 3
            shy prop Hidden = 4
        }
        """;

    [Fact]
    public async Task Only_reachable_properties_are_listed()
    {
        Assert.Equal(["Open"], await ListedMembersAsync(Modifiers));
    }

    [Theory]
    // Each modifier that member access refuses from outside, and previously appeared anyway.
    [InlineData("Local")]
    [InlineData("Guard")]
    [InlineData("Hidden")]
    public async Task A_hidden_modifier_keeps_its_property_out_of_the_listing(string name)
    {
        Assert.DoesNotContain(name, await ListedMembersAsync(Modifiers));
    }

    [Fact]
    public async Task A_static_property_is_still_listed()
    {
        // A static is a real member of the type even though it is not an instance one, so the
        // instance rule must not be applied to it.
        Assert.Contains("S", await ListedMembersAsync("class C { static prop S = 1\n    prop Open = 2 }"));
    }

    [Fact]
    public async Task An_ordinary_class_is_unaffected()
    {
        Assert.Equal(["X", "Y"], await ListedMembersAsync("class C { prop X = 1\n    prop Y = 2 }"));
    }
}

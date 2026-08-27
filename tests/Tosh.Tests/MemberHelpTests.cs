using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `help &lt;Type&gt;.&lt;member&gt;` — `TS-P2-101`'s remaining half.
///
/// The type-level half landed in August: a class's `##` comment reaches `help Swatch`.
/// A *member's* did not, and could not — `help K.P` answered "was not found" for every
/// type, because there was no member resolution path at all. The comment also stopped
/// earlier than the descriptors: neither `ToshClassPropertyDefinition` nor
/// `ToshClassMethodDefinition` carried a `DocComment`, so the text died at the runtime
/// definition and never reached anything that could surface it.
///
/// Asserted on the topic rather than on rendered output: `help` yields a topic object the
/// CLI renders, so capturing stdout measures the renderer — a mistake the type-level half
/// records making, where a class printed and a struct produced nothing and it read as a
/// missing fix rather than a mis-aimed probe.
/// </summary>
public sealed class MemberHelpTests
{
    private static async Task<HelpTopic?> ResolveAsync(string source, string topic)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        await engine.ExecuteToListAsync(source);

        return HelpCatalog.ResolveTopic(runtime, topic);
    }

    private const string Documented = """
        ## A colour swatch.
        class HelpSwatch {
            ## The colour's name.
            prop Name: string = ""

            ## Brightens the swatch.
            func Brighten(amount: int) -> int => $amount
        }
        """;

    /// <summary>A documented property's own summary.</summary>
    [Fact]
    public async Task A_documented_property_has_its_own_help()
    {
        var topic = await ResolveAsync(Documented, "HelpSwatch.Name");

        Assert.NotNull(topic);
        Assert.Equal("The colour's name.", topic!.Description);
    }

    /// <summary>And a documented method's.</summary>
    [Fact]
    public async Task A_documented_method_has_its_own_help()
    {
        var topic = await ResolveAsync(Documented, "HelpSwatch.Brighten");

        Assert.NotNull(topic);
        Assert.Equal("Brightens the swatch.", topic!.Description);
    }

    /// <summary>
    /// An undocumented member still gets a synthesised line. An empty help entry is worse
    /// than a generic one — the same rule the type-level half settled on.
    /// </summary>
    [Fact]
    public async Task An_undocumented_member_still_gets_a_description()
    {
        var topic = await ResolveAsync("class HelpPlain { prop P: int = 0 }", "HelpPlain.P");

        Assert.NotNull(topic);
        Assert.Contains("P", topic!.Description, StringComparison.Ordinal);
        Assert.Contains("HelpPlain", topic.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A member that does not exist is still not found, so the new path cannot answer for
    /// anything it should not.
    /// </summary>
    [Fact]
    public async Task A_missing_member_is_still_not_found()
        => Assert.Null(await ResolveAsync("class HelpPlain { prop P: int = 0 }", "HelpPlain.Nope"));

    /// <summary>
    /// The type-level topic is unchanged — the control for adding a member path beneath it.
    /// </summary>
    [Fact]
    public async Task The_type_topic_is_unchanged()
    {
        var topic = await ResolveAsync(Documented, "HelpSwatch");

        Assert.NotNull(topic);
        Assert.Equal("A colour swatch.", topic!.Description);
    }

    /// <summary>
    /// A hidden member is not advertised, matching what `members` and `methods` show.
    /// </summary>
    [Fact]
    public async Task A_shy_member_is_not_offered()
        => Assert.Null(await ResolveAsync(
            "class HelpShy { shy prop Secret: int = 0 }",
            "HelpShy.Secret"));
}

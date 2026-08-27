using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>from xml</c> yields shell data, and <c>to xml | from xml</c> round-trips —
/// <c>TS-P1-44</c>.
/// </summary>
/// <remarks>
/// <para>
/// It used to yield the <see cref="System.Xml.Linq.XDocument"/>, so the display engine
/// reflected over it and printed <c>NodeType</c>, <c>BaseUri</c>, <c>Parent</c>,
/// <c>FirstNode</c> and <c>&lt;cycle&gt;</c> markers where the tree points back at itself.
/// Every other reader in the family converts, so XML was the only <c>to</c>/<c>from</c>
/// pair that did not round-trip.
/// </para>
/// <para>
/// The document is still reachable through <c>--raw</c>. Handing it over was a deliberate
/// choice with a test pinning it — it is how a caller reaches the XML API — so what
/// changed is which of the two is the default, not the removal of one.
/// </para>
/// </remarks>
public sealed class XmlConversionTests
{
    private static async Task<string> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        var results = await engine.ExecuteToListAsync(script);
        return string.Join("\n", results.Select(v => v?.ToString() ?? string.Empty)).Trim();
    }

    [Fact]
    public async Task To_xml_then_from_xml_round_trips()
    {
        // The reported case. Non-ASCII and a comma are in there because they are what
        // TS-P2-70 and the CSV quoting rules turn on elsewhere.
        var json = await RunAsync("""{| a = "TōSh", b = "x,y" |} | to xml | from xml | to json --compact""");

        Assert.Equal("""{"a":"TōSh","b":"x,y"}""", json);
    }

    [Fact]
    public async Task An_element_of_text_is_its_text()
    {
        Assert.Equal("""{"n":"x"}""", await RunAsync("""'<r><n>x</n></r>' | from xml | to json --compact"""));
    }

    [Fact]
    public async Task Repeated_siblings_become_a_list()
    {
        // The failure this avoids is silent: collapsing repeats to the last one loses
        // data without saying so.
        Assert.Equal(
            """{"i":["1","2","3"]}""",
            await RunAsync("""'<r><i>1</i><i>2</i><i>3</i></r>' | from xml | to json --compact"""));
    }

    [Fact]
    public async Task A_single_occurrence_is_not_wrapped_in_a_list()
    {
        Assert.Equal("""{"i":"1"}""", await RunAsync("""'<r><i>1</i></r>' | from xml | to json --compact"""));
    }

    [Fact]
    public async Task Attributes_are_prefixed_so_they_cannot_collide()
    {
        // `@` is not legal at the start of an element name, so an attribute can never
        // shadow a child of the same name.
        Assert.Equal(
            """{"@id":"7","n":"x"}""",
            await RunAsync("""'<r id="7"><n>x</n></r>' | from xml | to json --compact"""));
    }

    [Fact]
    public async Task Text_alongside_attributes_becomes_hash_text()
    {
        Assert.Equal(
            """{"@id":"7","#text":"hello"}""",
            await RunAsync("""'<r id="7">hello</r>' | from xml | to json --compact"""));
    }

    [Fact]
    public async Task An_empty_element_is_null()
    {
        Assert.Equal("""{"n":null}""", await RunAsync("""'<r><n/></r>' | from xml | to json --compact"""));
    }

    [Fact]
    public async Task Nesting_survives()
    {
        Assert.Equal(
            """{"a":{"b":{"c":"deep"}}}""",
            await RunAsync("""'<r><a><b><c>deep</c></b></a></r>' | from xml | to json --compact"""));
    }

    [Fact]
    public async Task The_raw_document_is_still_available()
    {
        Assert.Equal("root", await RunAsync("""'<root><i/></root>' | from xml --raw | get "Root.Name.LocalName" """));
    }

    [Fact]
    public async Task Malformed_xml_still_reports_a_parse_failure()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync("""'<r><unclosed>' | from xml"""));

        Assert.Contains("XML", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

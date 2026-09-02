using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `--typed` for json, toml, xml and csv — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// The tag convention generalises across formats; fidelity does not, because the formats differ
/// in what they can represent. The rule is that a typed write <b>round-trips or refuses</b> — no
/// format silently degrades while claiming to be typed. CSV is where that bites: it has no
/// nesting, so a nested value is refused rather than flattened into columns nobody can read back.
/// </para>
/// <para>
/// Placement is decided once, in <c>ShellDataSerializer.Normalize</c>, because every format
/// reaches that one method. Each format then renders the tag the way its own syntax wants: a
/// sibling key in json and toml, a column in csv, and an <i>attribute</i> in xml — where `$` is
/// not a legal name character at all, so a sanitised `__type` element would have been a tag no
/// XML reader could recognise.
/// </para>
/// </remarks>
public sealed class TypedFormatTests
{
    private const string Prelude =
        """
        record TfExchange(Item: string, Amount: int)
        enum TfProfession { Librarian, Farmer }
        class TfVillager {
            prop Name = ""
            prop Trades = []
        }

        """;

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(Prelude + source);
        return string.Join("\n", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── The tag, per format ────────────────────────────────────────────────────

    [Fact]
    public async Task Json_tags_a_declared_type_with_a_sibling_key()
    {
        Assert.Equal(
            """{"$type":"TfExchange","Item":"Emerald","Amount":1}""",
            await RunAsync("""new TfExchange(Item = "Emerald", Amount = 1) | to json --typed --compact"""));
    }

    [Fact]
    public async Task Toml_tags_with_a_key_in_the_table()
    {
        var toml = await RunAsync("""new TfExchange(Item = "Emerald", Amount = 1) | to toml --typed""");

        Assert.Contains("\"$type\" = \"TfExchange\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xml_tags_with_an_attribute_because_the_key_is_not_a_legal_name()
    {
        Assert.Equal(
            """<root type="TfExchange"><Item>Emerald</Item><Amount>1</Amount></root>""",
            await RunAsync("""new TfExchange(Item = "Emerald", Amount = 1) | to xml --typed --compact"""));
    }

    [Fact]
    public async Task Csv_tags_with_a_column()
    {
        var csv = await RunAsync("""new TfExchange(Item = "Emerald", Amount = 1) | to csv --typed""");

        Assert.StartsWith("$type,Item,Amount", csv, StringComparison.Ordinal);
        Assert.Contains("TfExchange,Emerald,1", csv, StringComparison.Ordinal);
    }

    // ── Untagged is unchanged ──────────────────────────────────────────────────

    [Theory]
    [InlineData("to json --compact", """{"Item":"Emerald","Amount":1}""")]
    [InlineData("to xml --compact", "<root><Item>Emerald</Item><Amount>1</Amount></root>")]
    public async Task Without_the_flag_nothing_changes(string verb, string expected)
    {
        Assert.Equal(expected, await RunAsync($"""new TfExchange(Item = "Emerald", Amount = 1) | {verb}"""));
    }

    // ── Nesting ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tag_nests_with_the_value()
    {
        // The tag is placed by the shared normalizer, so it reaches every level rather than
        // only the root — which is what makes a nested document reconstructible.
        Assert.Equal(
            """{"$type":"TfVillager","Name":"Steve","Trades":[{"$type":"TfExchange","Item":"a","Amount":1}]}""",
            await RunAsync(
                """
                new TfVillager {| Name = "Steve", Trades = [new TfExchange(Item = "a", Amount = 1)] |}
                    | to json --typed --compact
                """));
    }

    [Fact]
    public async Task Csv_refuses_a_nested_value_rather_than_flattening_it()
    {
        // The rule that keeps `--typed` honest. CSV cannot nest, so the alternative to refusing
        // is a document that claims to be typed and cannot be read back.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            new TfVillager {| Name = "Steve", Trades = [new TfExchange(Item = "a", Amount = 1)] |}
                | to csv --typed
            """));

        Assert.Contains("Trades", error.Message, StringComparison.Ordinal);
        Assert.Contains("CSV cannot represent", error.Message, StringComparison.Ordinal);
    }

    // ── Reading the tag back ───────────────────────────────────────────────────

    [Fact]
    public async Task A_typed_record_reads_back_as_itself()
    {
        // The claim `--typed` makes on write, honoured on read. Without this the flag wrote a
        // promise nothing kept, which is worse than not tagging.
        Assert.Equal("Emerald\nTfExchange", await RunAsync(
            """
            var j = (new TfExchange(Item = "Emerald", Amount = 1) | to json --typed)
            var back = (from json --typed $j)
            echo $back.Item
            echo $back.ShellTypeName
            """));
    }

    [Fact]
    public async Task A_typed_enum_reads_back_to_the_same_member()
    {
        // Identity, not just the name: the rebuilt value is the member, so it compares equal to
        // the one that was written.
        Assert.Equal("True", await RunAsync(
            """
            var j = (TfProfession::Librarian | to json --typed)
            echo ((from json --typed $j) == TfProfession::Librarian)
            """));
    }

    [Fact]
    public async Task A_typed_class_reads_back_as_itself()
    {
        Assert.Equal("Steve", await RunAsync(
            """
            var j = (new TfVillager {| Name = "Steve" |} | to json --typed)
            echo ((from json --typed $j).Name)
            """));
    }

    [Fact]
    public async Task Reading_without_the_flag_is_unchanged()
    {
        // The tag is inert to an untyped read: it comes back as an ordinary field.
        Assert.Equal("Emerald", await RunAsync(
            """
            var j = (new TfExchange(Item = "Emerald", Amount = 1) | to json)
            echo ((from json $j).Item)
            """));
    }

    [Fact]
    public async Task A_tag_naming_a_clr_type_is_refused()
    {
        // The rule that matters most here: JSON is the format that actually receives untrusted
        // input, so a document must not be able to name a type whose construction does something.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            var j = (read-file "/dev/null")
            from json --typed "{\"$type\": \"System.Text.StringBuilder\", \"A\": 1}"
            """));

        Assert.Contains("System.Text.StringBuilder", error.Message, StringComparison.Ordinal);
        Assert.Contains("not a type this program declares", error.Message, StringComparison.Ordinal);
    }

    // ── A boolean flag must not eat the document ───────────────────────────────

    [Fact]
    public async Task A_valueless_flag_does_not_consume_the_following_argument()
    {
        // `from` assumed every flag took a value, which was invisible while all of them did —
        // `-d ,` and friends. `--typed $document` read the document as the flag's value and then
        // reported that no text had been supplied, naming the argument that was right there.
        Assert.Equal("Emerald", await RunAsync(
            """
            var j = (new TfExchange(Item = "Emerald", Amount = 1) | to json --typed)
            echo ((from json --typed $j).Item)
            """));
    }

    [Fact]
    public async Task A_value_taking_flag_still_takes_its_value()
    {
        // The control for the fix above: `-d` must still consume its delimiter.
        Assert.Equal("1", await RunAsync(
            """
            echo ((from csv -d "," "a,b\n1,2" | first).a)
            """));
    }

    // ── An enum has no fields to sit beside ────────────────────────────────────

    [Fact]
    public async Task A_tagged_enum_becomes_an_object_so_it_can_name_its_type()
    {
        // Untagged it is a bare string and the enum it belongs to is lost — the gap
        // `TOAST-0088` made legible but could not close. The shape change is what the flag
        // opts into.
        Assert.Equal(
            """{"$type":"TfProfession","$value":"Librarian"}""",
            await RunAsync("TfProfession::Librarian | to json --typed --compact"));
    }

    [Fact]
    public async Task A_tagged_enum_is_an_attribute_and_text_in_xml()
    {
        Assert.Equal(
            """<root type="TfProfession">Librarian</root>""",
            await RunAsync("TfProfession::Librarian | to xml --typed --compact"));
    }
}

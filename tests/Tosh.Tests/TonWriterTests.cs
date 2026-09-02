using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Writing a value as Tōast Object Notation — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// TON is the subset of Tōast's own value syntax that means something without a schema. The
/// property every test here ultimately serves is the round trip at the bottom: a document this
/// writer produces is <i>ordinary Tōast source</i>, so reading one needs no second parser that
/// could drift from the first.
/// </para>
/// <para>
/// Reading is not implemented yet — `from ton` declines rather than half-working, because it has
/// to resolve names against the program's declared types and reject everything outside the
/// notation before a value is built, and `IDataFormat` carries neither the engine nor the parser.
/// </para>
/// </remarks>
public sealed class TonWriterTests
{
    private const string Prelude =
        """
        record TonExchange(Item: string, Amount: int)
        enum TonProfession { Librarian, Farmer }
        union TonWrapped { Wrapped(value: int) }
        class TonVillager {
            prop Name = ""
            prop Job = TonProfession::Farmer
        }
        class TonBox<T> { prop Item = null }
        class TonDerived {
            prop N = 2
            prop Doubled => ($this.N * 2)
        }

        """;

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(Prelude + source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── Scalars and collections ────────────────────────────────────────────────

    [Theory]
    [InlineData("to ton 5", "5")]
    [InlineData("to ton \"hi\"", "\"hi\"")]
    [InlineData("to ton true", "true")]
    [InlineData("to ton null", "null")]
    [InlineData("to ton [1, 2, 3]", "[1, 2, 3]")]
    [InlineData("to ton 483.06`MW", "483.06`MW")]
    public async Task A_scalar_writes_as_its_own_literal(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_quantity_is_one_value_not_a_shape()
    {
        // `Quantity` implements `IShellRecordObject`, so without an explicit case ahead of that
        // one it wrote out its magnitude, unit, category and dimension as fields — four pieces
        // of a value that has a literal of its own.
        Assert.Equal("483.06`MW", await RunAsync("to ton 483.06`MW"));
    }

    // ── Declared shapes ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_record_writes_named_constructor_arguments()
    {
        // Named, never positional: a record's field order is not part of its meaning, so
        // `new TonExchange("Emerald", 1)` would silently corrupt every document the day someone
        // reorders the declaration.
        Assert.Equal(
            """new TonExchange(Item = "Emerald", Amount = 1)""",
            await RunAsync("""to ton (new TonExchange(Item = "Emerald", Amount = 1))"""));
    }

    [Fact]
    public async Task A_class_writes_a_typed_literal()
    {
        Assert.Equal(
            """new TonVillager {| Name = "Steve", Job = TonProfession::Librarian |}""",
            await RunAsync(
                """to ton (new TonVillager {| Name = "Steve", Job = TonProfession::Librarian |})"""));
    }

    [Fact]
    public async Task An_enum_member_writes_as_a_path()
    {
        // A path, not a member access — `TOAST-0090`'s operator is what lets the safety rule be
        // syntactic rather than semantic.
        Assert.Equal("TonProfession::Librarian", await RunAsync("to ton TonProfession::Librarian"));
    }

    [Fact]
    public async Task A_variant_is_written_positionally_even_when_its_field_is_named()
    {
        // Field names in a variant declaration are for pattern matching and member access.
        // Construction takes positions, so writing `Wrapped(value = 7)` emitted a document the
        // language could not read back — caught by the conformance corpus, which is what a
        // corpus is for.
        Assert.Equal("TonWrapped::Wrapped(7)", await RunAsync("to ton (TonWrapped::Wrapped(7))"));
    }

    [Fact]
    public async Task A_variant_with_a_declared_field_round_trips()
    {
        Assert.Equal("7", await RunAsync(
            """
            var doc = (to ton (TonWrapped::Wrapped(7)))
            echo ((from ton $doc).value)
            """));
    }

    [Fact]
    public async Task A_positional_variant_is_written_positionally()
    {
        // `Some(T)` declares no field name; `Item1` is synthesised and does not read back as the
        // field it stands for. Such a variant is positional by declaration, so there is no order
        // for a later edit to permute — the risk that makes named fields mandatory for a record.
        Assert.Equal("Option::Some(5)", await RunAsync("to ton (Option::Some(5))"));
    }

    // ── Type arguments, only where the payload cannot supply them ──────────────

    [Theory]
    [InlineData("to ton (Option::Some(5))", "Option::Some(5)")]
    [InlineData("to ton (Option::None<int>())", "Option::None<int>()")]
    [InlineData("to ton (Result::Ok<int, string>(3))", "Result::Ok<int, string>(3)")]
    public async Task Type_arguments_are_written_only_when_unrecoverable(string source, string expected)
    {
        // `Some(5)` pins `T` from its payload. `None()` pins nothing, and `Ok(3)` says nothing
        // about `E` — so both carry theirs. Decided 2026-08-29: the shortest spelling that still
        // reconstructs without a target type, which a heterogeneous stream cannot supply.
        Assert.Equal(expected, await RunAsync(source));
    }

    // ── The property all of it serves ──────────────────────────────────────────

    [Fact]
    public async Task A_written_document_is_valid_tosh_that_rebuilds_the_value()
    {
        // The whole premise: one grammar, one parser. What the writer emits is source, so
        // evaluating it is reading it.
        var document = await RunAsync(
            """
            to ton (new TonVillager {| Name = "Steve", Job = TonProfession::Librarian |})
            """);

        var rebuilt = await RunAsync($"var v = {document}\necho $v.Name\necho $v.Job");

        Assert.Equal("Steve,Librarian", rebuilt);
    }

    [Fact]
    public async Task A_nested_document_rebuilds_too()
    {
        var document = await RunAsync(
            """
            to ton [new TonExchange(Item = "Emerald", Amount = 1), new TonExchange(Item = "Book", Amount = 2)]
            """);

        var rebuilt = await RunAsync($"var xs = {document}\necho $xs[1].Item");

        Assert.Equal("Book", rebuilt);
    }

    // ── A value TON cannot name is still writable ──────────────────────────────

    [Fact]
    public async Task A_value_with_no_declared_name_becomes_an_anonymous_record()
    {
        // `ls | to ton` used to refuse: a `FileSystemEntry` is neither a declared shape nor a
        // scalar. TON already has a spelling for "an object whose type I cannot name" — the
        // anonymous record — and it is what `to json` does with the same value.
        var document = await RunAsync("ls /etc/hostname | to ton");

        Assert.StartsWith("{|", document, StringComparison.Ordinal);
        Assert.Contains("Name = \"hostname\"", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unnameable_value_still_round_trips()
    {
        Assert.Equal("hostname", await RunAsync(
            """
            var doc = (ls /etc/hostname | to ton)
            echo ((from ton $doc).Name)
            """));
    }

    [Fact]
    public async Task A_date_is_written_in_a_round_trip_format()
    {
        // `ToString()` gave "1/8/2026 8:57:52 PM -05:00" — unparseable elsewhere and different
        // on a machine with other regional settings, which is not a thing a notation may do.
        var document = await RunAsync("ls /etc/hostname | to ton");

        Assert.Matches(@"Modified = ""\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", document);
    }

    // ── Generic type arguments ─────────────────────────────────────────────────

    [Theory]
    [InlineData("int", "5", "new TonBox<int> {| Item = 5 |}")]
    [InlineData("string", "\"hi\"", "new TonBox<string> {| Item = \"hi\" |}")]
    public async Task A_generic_class_carries_its_type_arguments(
        string argument, string item, string expected)
    {
        // Dropping them wrote a `Box<string>` and a `Box<int>` as the same document. The
        // spelling is the language's — `int`, not the descriptor's `Int32` — because a notation
        // whose whole rule is that it names no CLR type should not name one here either.
        Assert.Equal(expected, await RunAsync($"to ton (new TonBox<{argument}> {{| Item = {item} |}})"));
    }

    [Fact]
    public async Task A_generic_value_round_trips_with_its_argument()
    {
        Assert.Equal("5", await RunAsync(
            """
            var doc = (to ton (new TonBox<int> {| Item = 5 |}))
            echo ((from ton $doc).Item)
            """));
    }

    [Fact]
    public async Task A_non_generic_class_gains_no_brackets()
    {
        Assert.Equal(
            """new TonVillager {| Name = "Steve", Job = TonProfession::Librarian |}""",
            await RunAsync(
                """to ton (new TonVillager {| Name = "Steve", Job = TonProfession::Librarian |})"""));
    }

    // ── Computed properties are state nobody stored ────────────────────────────

    [Fact]
    public async Task A_computed_property_is_not_written()
    {
        // `prop Doubled => …` is derived. Writing it asserted a value nothing accepts back, and
        // one that goes wrong the moment a reader edits `N` by hand — which is exactly what a
        // notation invites them to do.
        Assert.Equal("new TonDerived {| N = 2 |}", await RunAsync("to ton (new TonDerived {| N = 2 |})"));
    }

    // ── Every declared shape round-trips ───────────────────────────────────────

    [Theory]
    [InlineData("new TonExchange(Item = \"a\", Amount = 1)", "$_.ShellTypeName", "TonExchange")]
    [InlineData("new TonVillager {| Name = \"s\" |}", "$_.ShellTypeName", "TonVillager")]
    [InlineData("TonWrapped::Wrapped(7)", "$_.value", "7")]
    [InlineData("TonProfession::Librarian", "$_.Name", "Librarian")]
    [InlineData("[1, 2, 3]", "(type-of $_)", "array<int>")]
    [InlineData("{: \"a\", \"b\" :}", "(type-of $_)", "set")]
    [InlineData("{% \"k\" => 3 %}", "$_[\"k\"]", "3")]
    [InlineData("483.06`MW", "$_.ToString()", "483.06 MW")]
    public async Task A_declared_shape_survives_a_round_trip(
        string expression, string probe, string expected)
    {
        // Written, read back, and still the same thing — the claim the notation exists to make
        // and the one no other format could.
        Assert.Equal(expected, await RunAsync(
            $$"""
            var doc = (to ton ({{expression}}))
            from ton $doc | each { echo {{probe}} }
            """));
    }

    [Fact]
    public async Task A_set_is_not_written_as_an_array()
    {
        // A set *is* an `IEnumerable`, so without its own case it was written `["a", "b"]` and
        // read back as an array — a shape change disguised as a round trip, which is the one
        // thing a notation must not do quietly.
        Assert.Equal("""{: "a", "b" :}""", await RunAsync("""to ton {: "a", "b" :}"""));
    }

    // ── Reading: what the notation admits ──────────────────────────────────────

    [Theory]
    [InlineData("""new TonExchange(Item = "Emerald", Amount = 1)""", "Emerald")]
    public async Task A_document_rebuilds_a_declared_value(string document, string expected)
    {
        Assert.Equal(expected, await RunAsync(
            $$"""
            var v = (from ton {{Quote(document)}})
            echo $v.Item
            """));
    }

    [Fact]
    public async Task A_document_may_be_a_collection_of_values()
    {
        Assert.Equal("Book", await RunAsync(
            """
            var xs = (from ton "[new TonExchange(Item = \"a\", Amount = 1), new TonExchange(Item = \"Book\", Amount = 2)]")
            echo $xs[1].Item
            """));
    }

    [Fact]
    public async Task An_index_access_is_not_part_of_the_notation()
    {
        // Reading a document must not evaluate anything, and `[…][0]` is evaluation however
        // harmless it looks. Index the value after it is built, in ordinary Tōast.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("""from ton "[1, 2, 3][0]" """));

        Assert.Contains("index access", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_path_rebuilds_an_enum_member()
    {
        Assert.Equal("Librarian", await RunAsync(
            """echo (from ton "TonProfession::Librarian")"""));
    }

    [Fact]
    public async Task A_written_document_reads_back_through_from_ton()
    {
        // Writer and reader meet: `to ton` then `from ton` is the round trip the item exists for.
        Assert.Equal("Steve", await RunAsync(
            """
            var doc = (to ton (new TonVillager {| Name = "Steve", Job = TonProfession::Librarian |}))
            var back = (from ton $doc)
            echo $back.Name
            """));
    }

    // ── Reading: what it refuses, and why ──────────────────────────────────────

    [Theory]
    [InlineData("$x", "a variable")]
    [InlineData("ls /", "a command")]
    [InlineData("1 + 1", "an operator")]
    [InlineData("$\"hi {$x}\"", "an interpolated string")]
    public async Task A_construct_outside_the_notation_is_named_and_refused(
        string document, string expected)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync($"from ton {Quote(document)}"));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clr_type_is_refused_even_though_tosh_would_construct_it()
    {
        // The rule that closes the `TypeNameHandling` class structurally: the attacker supplies
        // no code, only the name of a type whose construction does something. Only types this
        // program declares are admitted, so there is no blocklist to keep up to date.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("""from ton "new System.Text.StringBuilder()" """));

        Assert.Contains("System.Text.StringBuilder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_static_property_is_refused_on_the_kind_of_its_type()
    {
        // `Math::PI` looks exactly like `Profession::Librarian`. It is refused because `Math` is
        // not a type this program declared — on the kind of the type, not by naming the member,
        // so whatever .NET adds later is refused too.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("""from ton "System::Math::PI" """));

        Assert.Contains("System.Math", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_positional_constructor_argument_is_refused()
    {
        // Parses anywhere, means nothing without the field order — and reordering a record's
        // fields would silently corrupt every document that used positions.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("""
                var doc = "new TonExchange(\"Emerald\", 1)"
                from ton $doc
                """));

        Assert.Contains("positional", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

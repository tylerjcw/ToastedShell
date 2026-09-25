using Tosh.DevCompanion.Memory;

namespace Tosh.DevCompanion.Tests;

/// <summary>
/// The format of a shared memory's file, <c>.tosh/memories/&lt;id&gt;.toml</c>.
/// </summary>
/// <remarks>
/// These files are how memories travel between checkouts, so a byte lost on the way out is a
/// memory corrupted everywhere it is pulled. Everything written must read back exactly, and
/// anything that cannot be read — a hand edit outside the subset, a merge left half done —
/// must be refused with its line rather than guessed at.
/// </remarks>
public sealed class SharedMemoryFileTests
{
    private const string Id = "01a0daad-6cc6-73dc-9b3b-34e2c5b199a6";
    private const string OtherId = "01a0daad-6cd3-76a8-be67-d13bf03b07c7";

    private static SharedMemory Memory(string content = "body") => new(
        Id: Id,
        CreatedAt: 1_790_000_000_123,
        Category: "decision",
        Scope: "project",
        Source: "ai",
        Pinned: false,
        SessionId: null,
        Tags: [],
        Summary: "summary",
        Content: content,
        Links: [],
        DeletedAt: null);

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("ends with a newline\n")]
    [InlineData("CR\r inside, CRLF\r\n too")]
    [InlineData("tab\there, \"quotes\", \"\"\"triple\"\"\" and a \\ backslash")]
    [InlineData("control \u0001 \u001b \u007f characters")]
    [InlineData("non-ASCII: Tōsh — ≤120 🎉")]
    [InlineData("\"\"\"\nopens and closes with the delimiter\n\"\"\"")]
    [InlineData("ends in a backslash \\")]
    [InlineData("two lines, the last\nends in a backslash \\")]
    public void Content_round_trips_exactly(string content)
    {
        var parsed = SharedMemoryFile.Parse(SharedMemoryFile.Render(Memory(content), []));

        Assert.Equal(content, parsed.Memory.Content);
    }

    [Fact]
    public void Everything_a_memory_records_round_trips()
    {
        var memory = Memory("first line\nsecond line") with
        {
            Pinned = true,
            SessionId = "session-1",
            Tags = ["ci", "dotnet"],
            Links =
            [
                new MemoryLink("scripts/dotnet-major.sh", Line: 1, LineEnd: 9, Kind: "def"),
                new MemoryLink("AGENTS.md"),
            ],
            DeletedAt = 1_790_000_100_456,
        };
        MemoryRelation[] relations =
        [
            new(Id, OtherId, "supersedes", 1_790_000_000_200),
            new(Id, OtherId, "related_to", 1_790_000_000_300),
        ];

        var parsed = SharedMemoryFile.Parse(SharedMemoryFile.Render(memory, relations));

        Assert.Equivalent(memory, parsed.Memory, strict: true);
        Assert.Equivalent(relations, parsed.Relations, strict: true);
    }

    /// <summary>
    /// A file is rewritten only when what it says changes, so rendering what was read must
    /// give back the same bytes; otherwise every sync would churn every file.
    /// </summary>
    [Fact]
    public void Rendering_what_was_read_gives_the_same_text()
    {
        var text = SharedMemoryFile.Render(
            Memory("a\nmulti-line body\n") with { Tags = ["x"] },
            [new MemoryRelation(Id, OtherId, "supports", 1_790_000_000_200)]);

        var reread = SharedMemoryFile.Parse(text);

        Assert.Equal(text, SharedMemoryFile.Render(reread.Memory, reread.Relations));
    }

    [Fact]
    public void A_checkout_with_CRLF_line_endings_reads_the_same()
    {
        var memory = Memory("first\nsecond\n");
        var crlf = SharedMemoryFile.Render(memory, []).ReplaceLineEndings("\r\n");

        Assert.Equal(memory.Content, SharedMemoryFile.Parse(crlf).Memory.Content);
    }

    [Fact]
    public void An_unresolved_merge_conflict_is_reported_as_one()
    {
        var text = SharedMemoryFile.Render(Memory(), []).Replace(
            "summary = \"summary\"\n",
            "<<<<<<< HEAD\nsummary = \"ours\"\n=======\nsummary = \"theirs\"\n>>>>>>> other\n");

        var error = Assert.Throws<FormatException>(() => SharedMemoryFile.Parse(text));

        Assert.StartsWith("line 7: unresolved merge conflict", error.Message);
    }

    [Fact]
    public void Conflict_markers_inside_content_are_only_content()
    {
        const string content = "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> other";

        var parsed = SharedMemoryFile.Parse(SharedMemoryFile.Render(Memory(content), []));

        Assert.Equal(content, parsed.Memory.Content);
    }

    [Theory]
    [InlineData("[[other]]\nid = \"a\"\n", "line 1: unknown table [[other]]")]
    [InlineData("[memory]\nid = \"a\"\n", "line 1: only [[memory]] and [[relation]] tables are supported")]
    [InlineData("id = \"a\"\n", "line 1: a key appears before the first")]
    [InlineData("[[memory]]\nid = \"a\"\nid = \"b\"\n", "line 3: duplicate key 'id'")]
    [InlineData("[[memory]]\nid = \"a\"\ncreated = yesterday\n", "line 3: 'yesterday' is not a value this file uses")]
    [InlineData(
        "[[relation]]\nfrom = \"a\"\nto = \"b\"\nrelationship = \"supports\"\ncreated = 2026-01-01T00:00:00.000Z\n",
        "line 1: the file has no [[memory]]")]
    public void A_file_the_store_could_not_have_written_is_refused(string text, string reason)
    {
        var error = Assert.Throws<FormatException>(() => SharedMemoryFile.Parse(text));

        Assert.StartsWith(reason, error.Message);
    }

    [Fact]
    public void A_file_holds_one_memory()
    {
        var text = SharedMemoryFile.Render(Memory(), []) + "\n" +
                   SharedMemoryFile.Render(Memory() with { Id = OtherId }, []);

        var error = Assert.Throws<FormatException>(() => SharedMemoryFile.Parse(text));

        Assert.Equal("line 10: a file holds one [[memory]]", error.Message);
    }

    [Fact]
    public void A_missing_field_is_named_with_the_line_of_its_table()
    {
        var text = SharedMemoryFile.Render(Memory(), []).Replace("summary = \"summary\"\n", "");

        var error = Assert.Throws<FormatException>(() => SharedMemoryFile.Parse(text));

        Assert.Equal("line 1: [[memory]] needs 'summary' to be a non-empty string", error.Message);
    }

    /// <summary>
    /// A relation lives in the file of the memory it starts from. One from another memory
    /// means the file was assembled by hand or by a bad merge, and taking it would attach a
    /// relation to whichever memory happened to hold it.
    /// </summary>
    [Fact]
    public void A_relation_that_starts_from_another_memory_is_refused()
    {
        var text = SharedMemoryFile.Render(
            Memory(),
            [new MemoryRelation(OtherId, Id, "supports", 1_790_000_000_200)]);

        var error = Assert.Throws<FormatException>(() => SharedMemoryFile.Parse(text));

        Assert.Contains($"belongs in {OtherId}.toml", error.Message);
    }
}

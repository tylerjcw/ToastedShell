using Microsoft.Data.Sqlite;
using Tosh.DevCompanion.Memory;

namespace Tosh.DevCompanion.Tests;

/// <summary>
/// How the store keeps <c>.tosh/memories/</c> and its database in step.
/// </summary>
/// <remarks>
/// The database is local and git ignores it; the directory is what a pull, a merge or a branch
/// switch changes underneath a running server. Each test is a way the two drift apart, and the
/// failure it guards against is silent: a memory lost, resurrected, or written over.
/// </remarks>
public sealed class SharedMemoryDirectoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tosh-devcompanion-").FullName;

    private string Database => Path.Combine(_root, "memory.db");
    private string SharedDirectory => Path.Combine(_root, "memories");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<SqliteMemoryStore> OpenAsync(string? database = null, bool mirror = true) =>
        SqliteMemoryStore.OpenAsync(database ?? Database, mirror ? SharedDirectory : null);

    private static StoreRequest Shared(string content, string summary = "summary") =>
        new(content, summary, "decision", Visibility: "shared");

    private string FileOf(string id) => Path.Combine(SharedDirectory, SharedMemoryFile.FileName(id));

    private IEnumerable<string> Files() =>
        Directory.Exists(SharedDirectory)
            ? Directory.EnumerateFiles(SharedDirectory, "*.toml").Select(Path.GetFileName).OfType<string>()
            : [];

    [Fact]
    public async Task A_shared_memory_gets_a_file_and_a_private_one_does_not()
    {
        using var store = await OpenAsync();

        var shared = (await store.StoreAsync(Shared("shared body"))).Entry;
        var local = (await store.StoreAsync(new StoreRequest("private body", "summary", "note"))).Entry;
        await store.RelateAsync(shared.Id, local.Id, "related_to");

        Assert.Equal([SharedMemoryFile.FileName(shared.Id)], Files());
        Assert.DoesNotContain("[[relation]]", File.ReadAllText(FileOf(shared.Id)));
    }

    [Fact]
    public async Task A_fresh_database_starts_with_the_shared_memories()
    {
        string first, second;
        using (var store = await OpenAsync())
        {
            first = (await store.StoreAsync(Shared("multi\nline — Tōsh 🎉\r\n", "first"))).Entry.Id;
            second = (await store.StoreAsync(Shared("second body", "second"))).Entry.Id;
            await store.RelateAsync(second, first, "supports");
        }

        using var fresh = await OpenAsync(database: Path.Combine(_root, "fresh.db"));

        Assert.Equal("multi\nline — Tōsh 🎉\r\n", (await fresh.GetAsync(first))!.Content);
        Assert.Equal("shared", (await fresh.GetAsync(second))!.Visibility);
        Assert.Contains(await fresh.GetRelationsAsync(first),
            r => r.FromId == second && r.ToId == first && r.Relationship == "supports");
    }

    /// <summary>
    /// A pull, a revert or a branch switch that removes a memory's file. The memory stays in
    /// the database, private, and the next write must not put the file back — which it would
    /// if the store took its own database as the truth.
    /// </summary>
    [Fact]
    public async Task A_file_that_goes_away_makes_its_memory_private_and_stays_gone()
    {
        using var store = await OpenAsync();
        var gone = (await store.StoreAsync(Shared("removed upstream"))).Entry;

        File.Delete(FileOf(gone.Id));
        await store.StoreAsync(Shared("an unrelated change"));

        Assert.False(File.Exists(FileOf(gone.Id)));
        Assert.Equal("private", (await store.GetAsync(gone.Id))!.Visibility);
    }

    [Fact]
    public async Task A_file_that_arrives_mid_session_is_imported_and_kept()
    {
        using var store = await OpenAsync();
        await store.StoreAsync(Shared("already here"));

        const string pulled = "01a0ffff-0000-7000-8000-000000000001";
        File.WriteAllText(FileOf(pulled), """
            [[memory]]
            id = "01a0ffff-0000-7000-8000-000000000001"
            created = 2026-09-25T12:00:00.000Z
            category = "decision"
            summary = "pulled"
            content = "arrived by git pull"

            """);

        var recalled = await store.RecallAsync(new RecallRequest("arrived"));
        await store.StoreAsync(Shared("a later change"));

        Assert.Contains(recalled.Results, r => r.Entry.Id == pulled);
        Assert.True(File.Exists(FileOf(pulled)));
    }

    /// <summary>
    /// A merge that leaves conflict markers in one file. That file is neither imported nor
    /// written over — the store would replace both sides with its own view — but the rest of
    /// the directory keeps syncing, and the file is picked up once it parses, without a restart.
    /// </summary>
    [Fact]
    public async Task A_conflicted_file_is_reported_and_left_alone_until_it_parses()
    {
        using var store = await OpenAsync();
        var conflicted = (await store.StoreAsync(Shared("body", "ours"))).Entry;
        var original = File.ReadAllText(FileOf(conflicted.Id));
        var markers = original.Replace(
            "summary = \"ours\"\n",
            "<<<<<<< HEAD\nsummary = \"ours\"\n=======\nsummary = \"theirs\"\n>>>>>>> other\n");
        File.WriteAllText(FileOf(conflicted.Id), markers);

        var other = (await store.StoreAsync(Shared("synced while the other is conflicted"))).Entry;

        Assert.Contains("unresolved merge conflict", store.SharedMemoryProblem);
        Assert.Equal(markers, File.ReadAllText(FileOf(conflicted.Id)));
        Assert.True(File.Exists(FileOf(other.Id)));

        File.WriteAllText(FileOf(conflicted.Id), original.Replace("\"ours\"", "\"resolved\""));
        await store.ListAsync(new ListRequest());

        Assert.Null(store.SharedMemoryProblem);
        Assert.Equal("resolved", (await store.GetAsync(conflicted.Id))!.Summary);
    }

    [Fact]
    public async Task Unsharing_removes_the_file_and_forgetting_leaves_a_tombstone()
    {
        using var store = await OpenAsync();
        var unshared = (await store.StoreAsync(Shared("to unshare"))).Entry;
        var forgotten = (await store.StoreAsync(Shared("to forget"))).Entry;

        await store.UpdateAsync(new UpdateRequest(unshared.Id, Visibility: "private"));
        await store.ForgetAsync(new ForgetRequest(forgotten.Id));

        Assert.False(File.Exists(FileOf(unshared.Id)));
        Assert.Contains("\ndeleted = ", File.ReadAllText(FileOf(forgotten.Id)));
    }

    /// <summary>
    /// A file that cannot be written. The change is kept and written by the next write; it must
    /// not be undone by the store re-reading the old file as though something else had changed it.
    /// </summary>
    [Fact]
    public async Task A_failed_write_is_retried_not_undone()
    {
        using var store = await OpenAsync();
        var memory = (await store.StoreAsync(Shared("body", "before"))).Entry;

        // Where the write goes first; a directory there makes it fail on every platform.
        var blocker = Directory.CreateDirectory(FileOf(memory.Id) + ".tmp");
        await store.UpdateAsync(new UpdateRequest(memory.Id, Summary: "after"));

        Assert.Contains("could not write", store.SharedMemoryProblem);
        Assert.Contains("summary = \"before\"", File.ReadAllText(FileOf(memory.Id)));

        blocker.Delete();
        await store.ListAsync(new ListRequest());
        Assert.Equal("after", (await store.GetAsync(memory.Id))!.Summary);

        await store.StoreAsync(new StoreRequest("any later write", "summary", "note"));
        Assert.Contains("summary = \"after\"", File.ReadAllText(FileOf(memory.Id)));
        Assert.Null(store.SharedMemoryProblem);
    }

    [Fact]
    public async Task A_memory_shared_while_mirroring_was_off_reaches_the_directory_on_the_next_open()
    {
        string id;
        using (var unmirrored = await OpenAsync(mirror: false))
            id = (await unmirrored.StoreAsync(Shared("shared before there was a directory"))).Entry.Id;

        Assert.False(File.Exists(FileOf(id)));

        using var mirrored = await OpenAsync();
        Assert.True(File.Exists(FileOf(id)));
    }

    /// <summary>
    /// A version 7 id starts with its creation time, so memories stored together shared their
    /// first eight characters, and a short id built from those could not tell them apart.
    /// </summary>
    [Fact]
    public async Task Short_ids_of_memories_stored_together_differ_and_resolve()
    {
        using var store = await OpenAsync();
        var first = (await store.StoreAsync(new StoreRequest("one", "one", "note"))).Entry;
        var second = (await store.StoreAsync(new StoreRequest("two", "two", "note"))).Entry;

        Assert.NotEqual(first.ShortId, second.ShortId);
        Assert.Equal(first.Id, await store.ResolveIdAsync(first.ShortId));
        Assert.Equal(second.Id, await store.ResolveIdAsync(second.ShortId));
        Assert.Equal(first.Id, await store.ResolveIdAsync(first.Id[..23]));
    }
}

using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The published TON conformance corpus, run against this implementation — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// The corpus lives in <c>docs/spec/ton-conformance/</c> beside the specification, so it is a
/// thing another implementation can run rather than a set of assertions private to this one.
/// These tests exist to keep the two honest with each other: a corpus nobody executes drifts
/// from the implementation, and an implementation with no corpus cannot be conformed to.
/// </para>
/// <para>
/// The contract is <b>accept or refuse</b>, not resolution. A reader in another language cannot
/// know which types the producing program declared, so it may return a faithful unresolved tree
/// — the way a JSON reader handles a <c>$type</c> key. What it must not do is accept a document
/// <c>refuse/</c> rejects.
/// </para>
/// </remarks>
public sealed class TonConformanceTests
{
    private static string CorpusRoot
    {
        get
        {
            // Walk up to the repository root: the test binary runs several directories deep, and
            // the corpus is a published artefact rather than a copied test fixture.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null &&
                   !Directory.Exists(Path.Combine(directory.FullName, "docs", "spec", "ton-conformance")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "docs", "spec", "ton-conformance");
        }
    }

    public static TheoryData<string> Accepted() => Cases("accept");

    public static TheoryData<string> Refused() => Cases("refuse");

    private static TheoryData<string> Cases(string bucket)
    {
        var data = new TheoryData<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(CorpusRoot, bucket), "*.ton").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static async Task<IReadOnlyList<object?>> ReadAsync(string bucket, string name)
    {
        var prelude = await File.ReadAllTextAsync(Path.Combine(CorpusRoot, "prelude.tosh"));
        var document = await File.ReadAllTextAsync(Path.Combine(CorpusRoot, bucket, name));

        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await foreach (var _ in engine.EvaluateAsync(prelude, "<prelude>")) { }

        var results = new List<object?>();

        await foreach (var value in engine.EvaluateAsync(
            $"from ton {Quote(document)}", "<conformance>"))
        {
            results.Add(value);
        }

        return results;
    }

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task An_accepted_document_reads(string name)
    {
        var values = await ReadAsync("accept", name);

        // Every accepted case is a document that produces at least one value. A reader that
        // accepted one and produced nothing would pass a weaker test than this.
        Assert.NotEmpty(values);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_refused_document_is_refused(string name)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await ReadAsync("refuse", name));

        // Refused for a stated reason, not by happening to fail. A case that failed because the
        // corpus file was malformed would otherwise count as conforming.
        Assert.Contains("TON document", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_corpus_covers_both_outcomes()
    {
        // A corpus that lost its refuse cases would still pass every test above.
        Assert.NotEmpty(Accepted());
        Assert.NotEmpty(Refused());
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

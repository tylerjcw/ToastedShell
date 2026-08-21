using System.Text.RegularExpressions;

namespace Tosh.Tests;

/// <summary>
/// The specification says which of its sentences bind an implementation — `TOAST-0033`.
/// </summary>
/// <remarks>
/// <para>
/// The document had no conformance statement, no normative/informative distinction, and no
/// definition of "must" — a word it used a dozen times in ordinary prose. So a reader
/// implementing a backend could not tell a requirement from an explanation from a
/// description of a bug awaiting a fix, and `TOAST-0030`'s "the compiled backend does not
/// implement five of these" read as "fails five paragraphs" rather than "fails five
/// requirements".
/// </para>
/// <para>
/// These are guards on the legend rather than on the prose. What they can check is that
/// the legend exists, that behaviour marked as a defect is marked non-normatively, and
/// that such a mark still points at work that is actually outstanding.
/// </para>
/// </remarks>
public sealed class SpecificationConformanceTests
{
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string Specification() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/spec/toastscript-spec.tex"));

    /// <summary>The document states what binds an implementation.</summary>
    [Theory]
    [InlineData(@"\section*{Conformance}")]
    [InlineData(@"\label{sec:conformance}")]
    // The four kinds of binding text, and the three kinds that do not bind.
    [InlineData("Statements of behaviour are normative")]
    [InlineData("Examples are normative in their results")]
    [InlineData("marks a genuine choice")]
    [InlineData("Rationale")]
    public void The_specification_carries_a_conformance_section(string required)
        => Assert.Contains(required, Specification(), StringComparison.Ordinal);

    /// <summary>
    /// Behaviour recorded as a defect is marked non-normative, by construction.
    /// </summary>
    /// <remarks>
    /// `defectbox` carries "not normative" in its default title, so the marking cannot be
    /// applied without the reader seeing it. The alternative — a note that merely *says* it
    /// is not normative — is one edit away from losing the words.
    /// </remarks>
    [Fact]
    public void A_defect_box_announces_that_it_does_not_bind()
    {
        var spec = Specification();

        Assert.Contains(@"\newtcolorbox{defectbox}", spec, StringComparison.Ordinal);
        Assert.Contains("Known defect --- not normative", spec, StringComparison.Ordinal);

        // Deliberately no assertion about *how many* exist. A first version required at
        // least two and failed the moment one was fixed — `TOAST-0031` gave diagnostics a
        // Tōast name and removed its box, so a guard about the legend failed because the
        // language got better. The count is supposed to fall to zero; what matters is that
        // the boxes which do exist are well formed, which the next test checks.
    }

    /// <summary>
    /// Every defect box names an item, and every item it names is still open.
    /// </summary>
    /// <remarks>
    /// The guard this item was filed for. `§Errors and catch` described a diagnostic as
    /// answering "to the implementation type name it happens to have" and pointed at
    /// `TOAST-0029` — which closed the same day, making the sentence wrong within hours.
    /// Nothing caught it but re-reading.
    ///
    /// A closed item behind a defect box means one of two things, and both need a person:
    /// the defect was fixed and the text is stale, or the text points at the wrong item.
    /// </remarks>
    [Fact]
    public void A_defect_box_points_at_work_that_is_still_outstanding()
    {
        var spec = Specification();
        var itemsDirectory = Path.Combine(RepositoryRoot(), "docs/plan/items");

        foreach (Match box in Regex.Matches(
                     spec,
                     @"\\begin\{defectbox\}(.*?)\\end\{defectbox\}",
                     RegexOptions.Singleline))
        {
            var referenced = Regex.Matches(box.Groups[1].Value, @"(TOAST|TOSH|PLAN|TS)-[0-9P-]+")
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                referenced.Length > 0,
                "a defect box must name the item tracking the change, or the reader has " +
                "no way to find out whether it still applies:\n" + box.Groups[1].Value.Trim());

            foreach (var id in referenced)
            {
                var path = Path.Combine(itemsDirectory, $"{id}.md");
                Assert.True(File.Exists(path), $"{id} is named by a defect box but has no item file");

                var status = Regex.Match(File.ReadAllText(path), @"^status:\s*(\S+)", RegexOptions.Multiline);
                Assert.True(status.Success, $"{id} has no status");

                Assert.False(
                    string.Equals(status.Groups[1].Value, "complete", StringComparison.Ordinal),
                    $"{id} is complete, but a defect box still describes it as outstanding. " +
                    "Either the behaviour changed and the text is stale, or the box names " +
                    "the wrong item.");
            }
        }
    }

    /// <summary>
    /// A section that names an item outside a defect box is held to the same rule.
    /// </summary>
    /// <remarks>
    /// Prose naming an open item is how this document explains that something is going to
    /// change. Once the item closes, the sentence is describing a past that no reader
    /// shares.
    /// </remarks>
    [Fact]
    public void Prose_does_not_point_at_completed_items()
    {
        var spec = Specification();
        var itemsDirectory = Path.Combine(RepositoryRoot(), "docs/plan/items");
        var stale = new List<string>();

        foreach (Match reference in Regex.Matches(spec, @"(TOAST|TOSH|PLAN)-[0-9]{4}"))
        {
            // A LaTeX comment is a note to whoever edits the source, not a claim to a
            // reader, so a closed item is a perfectly good thing for one to cite.
            var lineStart = spec.LastIndexOf('\n', reference.Index) + 1;
            if (spec[lineStart..reference.Index].TrimStart().StartsWith('%'))
            {
                continue;
            }

            var path = Path.Combine(itemsDirectory, $"{reference.Value}.md");

            if (File.Exists(path) &&
                Regex.IsMatch(File.ReadAllText(path), @"^status:\s*complete", RegexOptions.Multiline))
            {
                stale.Add(reference.Value);
            }
        }

        Assert.True(
            stale.Count == 0,
            "the specification points at completed items, so what it says about them is " +
            "probably no longer true:\n  " + string.Join("\n  ", stale.Distinct()));
    }
}

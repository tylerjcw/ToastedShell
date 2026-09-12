using System.Text.RegularExpressions;
using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// Every code listing in the specification parses.
/// </summary>
/// <remarks>
/// <para>
/// The specification is 8,700 lines with 321 code listings, and nothing checked that
/// the code in it was code. Parsing them all found five things a reading had not: a
/// <c>throw</c> after <c>??</c> that the parser rejected while the same page
/// documented it beside a ternary that worked; an event body written one field per
/// line, which only parsed with semicolons; the handler clauses
/// <c>priority</c> and <c>when</c> in the order the specification itself writes them,
/// which was the one order the parser refused; a comma between event fields; and a
/// loop binding written <c>for $e in</c>, which is not how the language spells one.
/// </para>
/// <para>
/// Four of the five were fixed in the parser rather than in the prose. The
/// specification is the definition, so a disagreement between it and the
/// implementation is a defect in the implementation unless the prose is wrong about
/// the language's intent — and here it was not: nothing about <c>??</c>, an event
/// body, or three unordered modifier clauses implied the restriction that existed.
/// </para>
/// <para>
/// A listing that is deliberately not a whole program — an excerpt from inside a
/// <c>bind native</c> block, a body elided as <c>{ ... }</c>, input data — is marked
/// with a <c>% spec-check: fragment</c> comment on the line before it. The marker is
/// a LaTeX comment, so it does not render.
/// </para>
/// </remarks>
public sealed class SpecificationListingsParseTests
{
    private static string SpecificationPath() =>
        Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")),
            "docs/spec/toastscript-spec.tex");

    private sealed record Listing(int Line, string Body);

    private static IReadOnlyList<Listing> Listings(out int skipped)
    {
        var text = File.ReadAllText(SpecificationPath());
        var listings = new List<Listing>();
        skipped = 0;

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?<marker>%[ ]spec-check:[ ]fragment[^\n]*\n)?"
                     + @"\\begin\{lstlisting\}(?<options>\[[^\]]*\])?\r?\n(?<body>.*?)\\end\{lstlisting\}",
                     RegexOptions.Singleline))
        {
            // A listing explicitly in another language, and one marked as an excerpt.
            if (match.Groups["options"].Value.Contains("language=XML", StringComparison.Ordinal) ||
                match.Groups["marker"].Success)
            {
                skipped++;
                continue;
            }

            var line = text.Take(match.Groups["body"].Index).Count(c => c == '\n') + 1;
            listings.Add(new Listing(line, match.Groups["body"].Value));
        }

        return listings;
    }

    [Fact]
    public void Every_code_listing_in_the_specification_parses()
    {
        var listings = Listings(out _);

        var broken = listings
            .Select(listing => (listing, errors: ToshParser.Parse(listing.Body, "<spec>").Diagnostics))
            .Where(pair => pair.errors.Count > 0)
            .Select(pair =>
                $"toastscript-spec.tex:{pair.listing.Line}  {pair.errors[0].Code} — {pair.errors[0].Title}\n"
                + $"      {pair.listing.Body.Split('\n')[0].Trim()}")
            .ToArray();

        Assert.True(
            broken.Length == 0,
            $"{broken.Length} of {listings.Count} specification listings do not parse.\n"
            + "Either the language is wrong or the listing is — and if the listing is a\n"
            + "deliberate excerpt, mark it with `% spec-check: fragment --- <why>` on the\n"
            + "line before it.\n\n  " + string.Join("\n  ", broken));
    }

    /// <summary>
    /// The negative control. A check that skips everything would pass the assertion
    /// above, and the fragment marker is exactly the mechanism that could make that
    /// happen by accident.
    /// </summary>
    [Fact]
    public void The_check_covers_almost_every_listing()
    {
        var listings = Listings(out var skipped);

        Assert.True(listings.Count > 300,
            $"Only {listings.Count} listings were collected; the extraction is broken.");
        Assert.True(skipped <= 10,
            $"{skipped} listings are excluded from the parse check, which is more than the "
            + "handful of genuine excerpts — a marker has been used to silence a real failure.");
    }
}

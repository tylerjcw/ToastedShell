using System.Text.RegularExpressions;

namespace Tosh.Tests;

/// <summary>
/// Every <c>Example:</c> in the LSP keyword table must parse — <c>TS-P2-33</c>.
/// </summary>
/// <remarks>
/// <para>
/// Five entries documented syntax the language does not have. <c>let</c>, <c>pick</c>,
/// and <c>get</c> all described a leading-<c>for</c> comprehension —
/// <c>[for x in $items pick x * 2]</c> — which does not parse; comprehensions are
/// body-first with <c>&lt;|</c>. <c>pick</c> is not a comprehension clause at all but a
/// pipeline command. <c>when</c>'s example omitted the parameter list its form requires.
/// </para>
/// <para>
/// Hover text that fails when followed is worse than no hover text, and counting entries
/// had made this table look like the *best*-covered consumer. Reading three of them found
/// wrong syntax in all three, which is the argument for checking rather than counting.
/// </para>
/// <para>
/// The same shape as <c>The_specification_keyword_list_matches_the_registry</c>: a
/// document that claims something about the language is a consumer, and a consumer can be
/// checked.
/// </para>
/// </remarks>
public sealed class HoverExampleParityTests
{
    /// <summary>
    /// Snippets that are illustrative fragments rather than whole programs, with the
    /// reason each cannot be parsed standalone. Anything else in the table must parse.
    /// </summary>
    private static readonly Dictionary<string, string> NotStandalone = new(StringComparer.Ordinal)
    {
        // Trailing `...` placeholders and bare signatures are deliberate ellipsis in
        // prose, not code a reader is meant to paste.
    };

    [Fact]
    public void Every_hover_example_parses()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/Tosh.LanguageServices/ToshLanguageFeatures.cs"));

        // `Example: \`…\`` inside a keyword description. Backticked so the snippet
        // boundary is unambiguous.
        var examples = Regex.Matches(source, @"Example: `([^`]+)`")
            .Select(match => match.Groups[1].Value)
            .Where(snippet => !snippet.Contains("...", StringComparison.Ordinal))
            .Where(snippet => !NotStandalone.ContainsKey(snippet))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(examples);

        var failures = new List<string>();

        foreach (var snippet in examples)
        {
            var unescaped = snippet.Replace("\\\"", "\"", StringComparison.Ordinal);
            var result = Tosh.Language.Parsing.ToshParser.Parse(unescaped, "<hover-example>");

            if (result.Diagnostics.Count > 0)
            {
                failures.Add($"{unescaped}\n      {result.Diagnostics[0].Code} — {result.Diagnostics[0].Title}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Hover examples that do not parse. A reader who follows one of these gets an "
            + "error, which is worse than no example:\n  " + string.Join("\n  ", failures));
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}

using System.Text.RegularExpressions;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>OperatorSurface</c> agrees with the parser, and every consumer covers it —
/// <c>TS-P2-78</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>operators</c> half of <c>TS-P2-10</c>. Measured before the registry existed,
/// the MCP server's table omitted fifteen operators the language has — floor division
/// <c>//</c>, every compound assignment, <c>??</c>, <c>?.</c>, <c>&amp;&amp;</c>,
/// <c>||</c>, <c>&lt;|</c> and <c>..</c> — so a client asking what ToastScript supports
/// was told a smaller language than the one it was writing for.
/// </para>
/// <para>
/// The authority is the parser's precedence predicates, and the guard below reads them
/// out of the source rather than restating them, so the registry cannot quietly diverge
/// from what actually parses. Prose stays with the consumer that renders it, exactly as
/// with <c>LanguageSurface</c>; what must not differ is which operators exist.
/// </para>
/// </remarks>
public sealed class OperatorSurfaceParityTests
{
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    /// <summary>Symbols named by one of the parser's operator predicates.</summary>
    private static HashSet<string> ParserOperatorSymbols()
    {
        var source = ReadSource("src/Tosh.Language/Parsing/ToshParser.cs");
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var predicate in new[]
                 {
                     "IsAdditiveOperatorToken",
                     "IsMultiplicativeOperatorToken",
                     "IsExponentiationOperatorToken",
                     "IsComparisonOperatorToken",
                     "IsAssignmentOperatorToken",
                 })
        {
            var match = Regex.Match(
                source,
                $@"bool {predicate}\(SyntaxToken token\)\s*\{{(.*?)\n        \}}",
                RegexOptions.Singleline);

            Assert.True(match.Success, $"could not locate {predicate}");

            // Any literal, then filter — not a length-bounded one. `"([^"]{1,4})"`
            // misaligns the moment a longer literal appears: on
            // `"in" or "contains" or …` the bound cannot span `contains`, so the scanner
            // backtracks and matches the ` or ` *between* two literals as though it were
            // one, and the guard then reports a phantom operator named " or ".
            foreach (Match literal in Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\""))
            {
                var symbol = literal.Groups[1].Value;

                if (symbol.Length > 0 && !symbol.Any(char.IsWhiteSpace))
                {
                    symbols.Add(symbol);
                }
            }
        }

        return symbols;
    }

    [Fact]
    public void Every_operator_the_parser_names_is_in_the_registry()
    {
        // The direction that matters: an operator the language parses and the registry
        // does not know is one every consumer will omit, which is exactly how `//` went
        // missing from the MCP table.
        var missing = ParserOperatorSymbols()
            .Where(symbol => !OperatorSurface.Operators.ContainsKey(symbol))
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "The parser accepts operators the registry does not list:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void The_registry_holds_the_operators_that_were_missing()
    {
        // Named individually because these are the fifteen the audit found; a count would
        // not say which came back.
        foreach (var symbol in new[]
                 {
                     "//", "//=", "??=", "??", "?.", "**=", "+=", "-=",
                     "*=", "/=", "%=", "&&", "||", "<|", "..",
                 })
        {
            Assert.True(
                OperatorSurface.Operators.ContainsKey(symbol),
                $"`{symbol}` is missing from OperatorSurface");
        }
    }

    [Fact]
    public void The_mcp_operator_table_covers_every_operator()
    {
        // The consumer the item was filed against. Matched on the table's `name` field:
        // a first pass using substring matching reported `**=`, `*=` and `..` as present
        // because they occur inside other strings, which is how the gap was undercounted.
        var source = ReadSource("src/Tosh.Mcp/ToshMcpServer.cs");

        var listed = Regex.Matches(source, @"name = ""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = OperatorSurface.Operators.Keys
            .Where(symbol => !listed.Contains(symbol))
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "The MCP operator table omits operators the language has:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Word_operators_are_words_the_language_surface_also_knows()
    {
        // The two registries answer different questions about the same word: `and` is a
        // word the highlighter colours and an operator the MCP table describes. They must
        // at least agree it exists.
        var missing = OperatorSurface.WordOperators
            .Where(word => !LanguageSurface.Words.ContainsKey(word))
            .OrderBy(word => word, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Word operators the language-word registry does not know:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Categories_partition_the_table()
    {
        // Every operator lands in exactly one category, so a consumer grouping by
        // category renders all of them and none twice.
        var byCategory = Enum.GetValues<OperatorCategory>()
            .SelectMany(OperatorSurface.InCategory)
            .ToArray();

        Assert.Equal(OperatorSurface.Operators.Count, byCategory.Length);
        Assert.Equal(byCategory.Length, byCategory.Distinct(StringComparer.Ordinal).Count());
    }
}

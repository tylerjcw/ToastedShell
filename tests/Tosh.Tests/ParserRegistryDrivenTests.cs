using System.Text.RegularExpressions;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The parser asks <see cref="LanguageSurface"/> for family membership rather than
/// spelling families out — <c>TS-P2-23</c>, clause 2.
/// </summary>
/// <remarks>
/// <para>
/// Two of the parser's hardcoded lists were exactly registry families and are now
/// lookups: <c>IsSubcommandModifierKeyword</c>, which spelled
/// <c>eager|hidden|hollow|vital|default</c>, and the visibility check inside the
/// typed-declaration lookahead, which spelled <c>export|global|shy</c>. The first
/// mattered most — <c>TS-P2-10</c> added the <c>SubcommandModifier</c> kind precisely
/// because <c>eager</c> and <c>hidden</c> exist nowhere else, so keeping a second list in
/// the parser is how the two would drift apart again.
/// </para>
/// <para>
/// <b>Clause 2 as written over-promises, and this file is the honest scope.</b> It asks
/// for "keyword and construct recognition driven by the registry rather than by scattered
/// literal comparisons", but a registry can answer *family membership* and not *construct
/// dispatch*: it can say <c>class</c> is a <c>TypeDeclaration</c>, and it cannot say that
/// this branch is the one that parses a class. Rewriting single-word comparisons like
/// <c>Current.Text == "class"</c> against a registry would produce worse code, not better.
/// Families are what was drifting, and families are what moved.
/// </para>
/// </remarks>
public sealed class ParserRegistryDrivenTests
{
    /// <summary>
    /// The parser's source across every partial file it is split into.
    /// </summary>
    /// <remarks>
    /// `TOAST-0005` divided ToshParser.cs by concern. A scan bound to one path silently
    /// stops seeing whatever moved, and reports that as a pass or a failure depending on
    /// which way the comparison runs — neither of which is about the invariant.
    /// </remarks>
    private static string ParserSource()
    {
        var directory = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
            "src/Tosh.Language/Parsing");

        return string.Join(
            "\n",
            Directory.EnumerateFiles(directory, "ToshParser*.cs")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    [Fact]
    public void The_subcommand_modifier_family_is_not_spelled_out_again()
    {
        // Non-tautological: the predicate now *is* a registry lookup, so comparing it to
        // the registry would prove nothing. What can regress is someone reintroducing the
        // literal list, and that is what this catches.
        var source = ParserSource();

        Assert.DoesNotContain("\"eager\" or \"hidden\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hollow\" or \"vital\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_visibility_family_is_not_spelled_out_again()
    {
        Assert.DoesNotContain(
            "\"export\" or \"global\" or \"shy\"",
            ParserSource(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_subcommand_modifier_the_registry_knows_parses_as_one()
    {
        // Execution-validated, in this file's usual style: each word must reach the
        // subcommand parser rather than being read as a command name.
        foreach (var modifier in LanguageSurface.SubcommandModifiers)
        {
            var result = ToshParser.Parse($"{modifier} subcommand nm {{ }}", "<probe>");

            Assert.True(
                result.Diagnostics.Count == 0,
                $"`{modifier} subcommand nm {{ }}` did not parse:\n  " + string.Join(
                    "\n  ",
                    result.Diagnostics.Select(d => $"{d.Code} — {d.Title}")));
        }
    }

    [Fact]
    public void A_word_outside_the_family_is_not_taken_as_a_subcommand_modifier()
    {
        // The negative half. `static` is a member modifier and not a subcommand one, so
        // it must not be consumed as a prefix here — otherwise the lookup would be
        // accepting anything and the test above would be vacuous.
        Assert.DoesNotContain("static", LanguageSurface.SubcommandModifiers, StringComparer.Ordinal);

        var result = ToshParser.Parse("static subcommand nm { }", "<probe>");

        Assert.True(
            result.Diagnostics.Count > 0 || result.Statement is not SubcommandStatementSyntax,
            "`static` was consumed as a subcommand modifier, which the registry does not list");
    }

    [Fact]
    public void A_visibility_word_is_still_not_read_as_a_type_name()
    {
        // `export FOO = "bar"` is a command, not a typed declaration — the reason the
        // visibility check exists in the typed-declaration lookahead at all.
        foreach (var word in LanguageSurface.Words
                     .Where(pair => pair.Value.HasFlag(LanguageWordKind.VisibilityModifier))
                     .Select(pair => pair.Key))
        {
            var result = ToshParser.Parse($"{word} FOO = \"bar\"", "<probe>");

            Assert.True(
                result.Statement is not VariableDeclarationStatementSyntax,
                $"`{word} FOO = \"bar\"` was parsed as a typed declaration");
        }
    }
}

using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// A lone <c>#</c> opens a line comment only when whitespace or the end of the
/// line follows it. Glued to a non-space it is an ordinary bareword character,
/// so <c>#ff0000</c> and <c>issue#42</c> need no quoting — matching the way bash
/// and zsh treat <c>#</c> inside a word.
///
/// <c>##</c> is unconditional: doc comments and <c>##{ }##</c> blocks keep their
/// meaning regardless of what follows, and these tests pin that down so the
/// bareword rule can never erode them.
/// </summary>
public sealed class HashCommentBoundaryTests
{
    private static IReadOnlyList<SyntaxToken> Lex(string source)
        => new ToshLexer(source).Lex();

    private static IReadOnlyList<string> BarewordTexts(string source)
        => Lex(source)
            .Where(token => token.Kind == SyntaxTokenKind.Bareword)
            .Select(token => token.Text)
            .ToArray();

    // ── '#' glued to a word is a bareword character ──────────────────────

    [Theory]
    [InlineData("echo #ff0000", "#ff0000")]
    [InlineData("echo issue#42", "issue#42")]
    [InlineData("echo http://host/page#frag", "http://host/page#frag")]
    [InlineData("echo C#", "C#")]
    [InlineData("echo a#b#c", "a#b#c")]
    public void A_hash_glued_to_a_word_stays_in_the_bareword(string source, string expected)
    {
        Assert.Contains(expected, BarewordTexts(source));
    }

    [Fact]
    public void A_glued_hash_does_not_swallow_the_rest_of_the_line()
    {
        // The old lexer stopped at '#' unconditionally, so `issue#42 tail`
        // lost both the '#42' and everything after it.
        var barewords = BarewordTexts("echo issue#42 tail");

        Assert.Contains("issue#42", barewords);
        Assert.Contains("tail", barewords);
    }

    // ── '#' followed by whitespace or EOL is a comment ───────────────────

    [Theory]
    [InlineData("echo a # trailing comment")]
    [InlineData("echo a #\ttab-separated comment")]
    [InlineData("echo a #")]
    [InlineData("# whole line\necho a")]
    public void A_hash_followed_by_space_or_end_of_line_opens_a_comment(string source)
    {
        var lexer = new ToshLexer(source);
        lexer.Lex();

        Assert.NotEmpty(lexer.LineComments);
    }

    [Fact]
    public void A_comment_is_not_produced_for_a_glued_hash()
    {
        var lexer = new ToshLexer("echo #ff0000");
        lexer.Lex();

        Assert.Empty(lexer.LineComments);
    }

    [Fact]
    public void Every_recorded_comment_begins_with_hash_then_whitespace_or_ends()
    {
        // The formatter re-emits LineComment.Text verbatim and relies on this
        // invariant to round-trip: it can never emit a '#' that would re-lex
        // as a bareword.
        var lexer = new ToshLexer("# one\necho a # two\n#\necho b #\tthree");
        lexer.Lex();

        Assert.NotEmpty(lexer.LineComments);
        foreach (var comment in lexer.LineComments)
        {
            Assert.StartsWith("#", comment.Text, StringComparison.Ordinal);
            if (comment.Text.Length > 1)
            {
                Assert.True(
                    comment.Text[1] is ' ' or '\t',
                    $"comment text '{comment.Text}' must be '#' alone or '#' + whitespace");
            }
        }
    }

    // ── Doc and block comments are unaffected ────────────────────────────

    [Fact]
    public void A_doc_comment_still_lexes_as_a_doc_comment()
    {
        var tokens = Lex("## Adds two numbers.\nfunc add(a, b) { return ($a + $b) }");

        Assert.Contains(tokens, token => token.Kind == SyntaxTokenKind.DocComment);
    }

    [Fact]
    public void Doc_comment_tags_survive_the_bareword_rule()
    {
        var source = """
            ## Adds two numbers.
            ## @param=a first value
            ## @returns the sum
            func add(a, b) { return ($a + $b) }
            """;

        var docTokens = Lex(source)
            .Where(token => token.Kind == SyntaxTokenKind.DocComment)
            .ToArray();

        var doc = DocComment.Parse(docTokens);

        Assert.NotNull(doc);
        Assert.Equal("Adds two numbers.", doc!.Description);
        Assert.Equal("first value", doc.Parameters["a"]);
        Assert.Equal("the sum", doc.Returns);
    }

    [Fact]
    public void A_doc_comment_is_never_treated_as_a_bareword()
    {
        // '##' has no following space here — it must still be a doc comment,
        // not a '##Adds' bareword.
        var tokens = Lex("##Adds two numbers.\nfunc add(a, b) { return 1 }");

        Assert.Contains(tokens, token => token.Kind == SyntaxTokenKind.DocComment);
        Assert.DoesNotContain(BarewordTexts("##Adds two numbers."), text => text.StartsWith("##", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hash_divider_line_still_reads_as_a_doc_comment()
    {
        // '##########' starts with '##', so it keeps lexing exactly as it
        // did before the bareword rule landed.
        var tokens = Lex("##########\necho after");

        Assert.Contains(tokens, token => token.Kind == SyntaxTokenKind.DocComment);
    }

    [Fact]
    public void A_block_comment_is_still_skipped_whole()
    {
        var result = ToshParser.Parse(
            "echo before\n##{ closed, with #hash and # spaced }##\necho after",
            "<t>");

        Assert.Empty(result.Diagnostics);
    }

    // ── Shebang ──────────────────────────────────────────────────────────

    [Fact]
    public void A_shebang_on_the_first_line_is_skipped()
    {
        // '#!' is not whitespace-terminated, so without the explicit line-1
        // case it would lex as a bareword and break every shebang script.
        var result = ToshParser.Parse("#!/usr/bin/env tosh\necho hello", "<t>");

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(BarewordTexts("#!/usr/bin/env tosh\necho hello"), text => text.Contains("#!"));
    }

    [Fact]
    public void A_hash_bang_after_the_first_line_does_not_swallow_the_line()
    {
        // Only offset 0 is a shebang. Later in the file '#!' is ordinary text,
        // so whatever follows it must still reach the token stream.
        Assert.Contains("x", BarewordTexts("echo before\necho #!x"));
    }

    // ── The shared rule itself ───────────────────────────────────────────

    [Theory]
    [InlineData("# comment", 0)]
    [InlineData("## doc", 0)]
    [InlineData("#", 0)]
    [InlineData("echo a # c", 7)]
    [InlineData("echo #ff0000", -1)]
    [InlineData("echo issue#42", -1)]
    [InlineData("echo C#", -1)]
    [InlineData("echo \"# not a comment\"", -1)]
    [InlineData("echo '# not a comment'", -1)]
    public void FindCommentStart_matches_the_lexer_rule(string line, int expected)
    {
        Assert.Equal(expected, ToshCommentSyntax.FindCommentStart(line));
    }

    [Theory]
    // A '#' that is not at the start of a word can never open a comment,
    // however it is followed — this is the POSIX half of the rule.
    [InlineData("a#")]
    [InlineData("a# ")]
    [InlineData("issue#42")]
    [InlineData("C#")]
    public void A_hash_mid_word_never_opens_a_comment(string line)
    {
        Assert.Equal(-1, ToshCommentSyntax.FindCommentStart(line));
    }
}

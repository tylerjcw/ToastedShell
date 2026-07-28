using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// A characterization corpus for the lexer (groundwork for
/// <c>TS-P2-11</c>). These tests pin what the lexer produces
/// <em>today</em>, including shapes that are known to be wrong. They are
/// not assertions that the current output is correct; their job is to
/// make every tokenization change visible and deliberate when the
/// bareword-versus-token boundary is reworked.
///
/// Entries that encode a known defect name the item that will change
/// them. When that item lands, the expectation is updated in the same
/// commit as the fix, so a redesign cannot quietly alter tokenization
/// somewhere unrelated.
/// </summary>
public sealed class LexerCharacterizationTests
{
    private static string Render(string source)
    {
        var tokens = new ToshLexer(source).Lex();
        return string.Join(
            " ",
            tokens
                .Where(token => token.Kind != SyntaxTokenKind.EndOfFile)
                .Select(token => $"{token.Kind}:{token.Text}"));
    }

    [Theory]
    // ---- shapes that already tokenize the way the language intends ----
    [InlineData("1 + 2", "Number:1 Bareword:+ Number:2")]
    [InlineData("$x + 1", "Bareword:$x Bareword:+ Number:1")]
    [InlineData("1 < 2", "Number:1 LessThan:< Number:2")]
    [InlineData("ls | where", "Bareword:ls Pipe:| Bareword:where")]
    [InlineData("a.b.c", "Bareword:a.b.c")]
    [InlineData("$x ?. Length", "Bareword:$x QuestionDot:?. Bareword:Length")]
    [InlineData("f(a = 1)", "Bareword:f OpenParen:( Bareword:a Bareword:= Number:1 CloseParen:)")]
    [InlineData("1..5", "Number:1 DotDot:.. Number:5")]
    // TS-P2-05, fixed 2026-07-26: valid separators still fold away, and
    // a leading underscore is an identifier rather than a number.
    [InlineData("1_000", "Number:1_000")]
    [InlineData("_1", "Bareword:_1")]
    // A float-headed range tokenizes correctly; `ToshRange` being
    // integer-only is a deliberate semantic decision enforced above the
    // lexer, not a tokenization defect.
    [InlineData("1.5..3", "Number:1.5 DotDot:.. Number:3")]
    // TS-P2-04, fixed 2026-07-26: the fused form now tokenizes like the
    // spaced form, so whitespace no longer changes meaning.
    [InlineData("$x?.Length", "Bareword:$x QuestionDot:?. Bareword:Length")]
    // TS-P2-15, fixed 2026-07-26: a named argument binds with or without
    // surrounding spaces.
    [InlineData("f(a=\"z\")", "Bareword:f OpenParen:( Bareword:a Bareword:= String:\"z\" CloseParen:)")]
    // Fixed 2026-07-26: `=>` is one token. It used to be none — the
    // bareword reader stopped at '>', leaving a `Bareword "="` and a stray
    // `GreaterThan` that every arrow site had to reassemble. (This was
    // labelled `TS-P2-25` here, which is a different item — the brace
    // disambiguation — so the ID has been dropped rather than guessed at.)
    [InlineData("1 => \"one\"", "Number:1 FatArrow:=> String:\"one\"")]
    [InlineData("1=>\"one\"", "Number:1 FatArrow:=> String:\"one\"")]
    [InlineData("default => 2", "Bareword:default FatArrow:=> Number:2")]
    // The neighbouring operators keep their own spellings.
    [InlineData("a >= b", "Bareword:a GreaterThanEqual:>= Bareword:b")]
    [InlineData("a == b", "Bareword:a Bareword:== Bareword:b")]
    [InlineData("echo hi > f.txt", "Bareword:echo Bareword:hi GreaterThan:> Bareword:f.txt")]
    [InlineData("--opt=value", "Bareword:--opt=value")]
    // TS-P2-25: paired collection-literal delimiters. `{` is a block and
    // nothing else; each literal announces itself with its own opener.
    [InlineData("{| a = 1 |}", "OpenBracePipe:{| Bareword:a Bareword:= Number:1 PipeCloseBrace:|}")]
    [InlineData("{: 1, 2 :}", "OpenBraceColon:{: Number:1 Comma:, Number:2 ColonCloseBrace::}")]
    [InlineData("{% \"k\" => 1 %}", "OpenBracePercent:{% String:\"k\" FatArrow:=> Number:1 PercentCloseBrace:%}")]
    // Empty forms need no special case: the two tokens simply abut.
    [InlineData("{||}", "OpenBracePipe:{| PipeCloseBrace:|}")]
    [InlineData("{::}", "OpenBraceColon:{: ColonCloseBrace::}")]
    [InlineData("{%%}", "OpenBracePercent:{% PercentCloseBrace:%}")]
    // A plain brace stays a plain brace.
    [InlineData("{ echo hi }", "OpenBrace:{ Bareword:echo Bareword:hi CloseBrace:}")]
    // Adjacency is the whole rule: a spaced form is not a delimiter, and is
    // diagnosed by the parser rather than silently reinterpreted.
    [InlineData("{ | a = 1 | }", "OpenBrace:{ Pipe:| Bareword:a Bareword:= Number:1 Pipe:| CloseBrace:}")]
    // An interior pipe keeps its meaning, because it is not adjacent to the
    // brace. This is the case that makes `|}` safe to claim.
    [InlineData("{| a = ls | count |}", "OpenBracePipe:{| Bareword:a Bareword:= Bareword:ls Pipe:| Bareword:count PipeCloseBrace:|}")]
    // Likewise an interior modulo against `%}`.
    [InlineData("{% \"k\" => $a % $b %}", "OpenBracePercent:{% String:\"k\" FatArrow:=> Bareword:$a Bareword:% Bareword:$b PercentCloseBrace:%}")]
    // `||` still wins where it should: the opener consumed the first pipe.
    [InlineData("$a || $b", "Bareword:$a DoublePipe:|| Bareword:$b")]
    public void Known_good_tokenization_is_pinned(string source, string expected)
    {
        Assert.Equal(expected, Render(source));
    }

    [Theory]
    // ---- shapes whose tokenization is the root cause of a filed defect ----

    // TS-P2-02: unary minus against a variable is absorbed into the
    // bareword, so the expression is read as a command name.
    [InlineData("-$x", "Bareword:-$x")]

    public void Known_defective_tokenization_is_pinned(string source, string expected)
    {
        // Pinned so the mode-switching rework shows exactly which of
        // these flip, rather than changing them incidentally.
        Assert.Equal(expected, Render(source));
    }

    [Theory]
    // Command-position words that must survive as barewords: these are
    // what a mode-switching lexer has to keep working while it starts
    // treating operators as real tokens in expression position.
    [InlineData("ls -la", "Bareword:ls Bareword:-la")]
    [InlineData("git commit -m", "Bareword:git Bareword:commit Bareword:-m")]
    [InlineData("./script.tosh", "Bareword:./script.tosh")]
    [InlineData("../parent", "Bareword:../parent")]
    [InlineData("*.txt", "Bareword:*.txt")]
    [InlineData("read-file", "Bareword:read-file")]
    [InlineData("group-by", "Bareword:group-by")]
    public void Command_position_barewords_are_pinned(string source, string expected)
    {
        Assert.Equal(expected, Render(source));
    }

    [Fact]
    public void Spacing_no_longer_changes_meaning_for_these_expressions()
    {
        // Both pairs used to tokenize differently, which is what made
        // TS-P2-11 a lexer problem before a parser problem. Under the
        // mode-tracking lexer they agree, so whitespace no longer
        // changes what these expressions mean.
        Assert.Equal(Render("$x?.Length"), Render("$x ?. Length"));
        Assert.Equal(Render("f(a=1)"), Render("f(a = 1)"));
        Assert.Equal(Render("{%a=>1%}"), Render("{% a => 1 %}"));

        // TS-P2-25. Record literals join the list: the literal openers enter
        // expression context, so `a=1` splits inside them. Under a bare `{`
        // it does not — `{ a=1 }` is one bareword and therefore a block,
        // while `{ a = 1 }` was a record. That inconsistency is what the
        // paired delimiters remove.
        Assert.Equal(Render("{|a=1|}"), Render("{| a = 1 |}"));
    }

    [Fact]
    public void No_source_still_produces_the_old_two_token_arrow()
    {
        // The pair form is what the parser used to reassemble. If any
        // input can still produce it, some arrow site will silently stop
        // matching, because the single-token check cannot see it.
        string[] corpus =
        [
            "1 => \"one\"",
            "1=>\"one\"",
            "func f(x) => $x",
            "func f(x)=>$x",
            "{ \"k\" => 1 }",
            "{k=>1}",
            "match (1) {\n    1 => \"one\"\n    default => \"other\"\n}",
            "[1] | each { _ => _ * 2 }",
            "class C { func d(x) => $x * 2 }",
        ];

        foreach (var source in corpus)
        {
            var tokens = new ToshLexer(source).Lex();
            for (var index = 0; index + 1 < tokens.Count; index++)
            {
                var pair = tokens[index].Kind == SyntaxTokenKind.Bareword
                           && tokens[index].Text == "="
                           && tokens[index + 1].Kind == SyntaxTokenKind.GreaterThan;

                Assert.False(pair, $"`{source}` still lexes an arrow as two tokens");
            }
        }
    }
}

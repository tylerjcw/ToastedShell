using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What quote characters inside a word mean — `TOSH-0001`.
///
/// A word *beginning* with a quote was lexed as a string and arrived unquoted. A word that
/// merely contained one was lexed whole and carried its quote characters all the way to the
/// callee, so
///
///     /usr/bin/grep -roh --include="*.cs" … src tests
///
/// searched for files literally named `"*.cs"` and reported **zero** matches where bash
/// reports 854. Nothing errored — the command succeeded and found nothing, which is the
/// worst shape this kind of defect takes, and it cost a full debugging pass to find.
///
/// Two halves, and both were needed. Quote *removal* is a rule about what a word's text is;
/// quote *awareness in the lexer* is what makes `--opt="a b"` one argument instead of two.
/// Fixing only the first would have left the second silently broken in the same way.
/// </summary>
public sealed class ShellWordQuotingTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join("|", results.Select(value => value?.ToString() ?? "null"));
    }

    // ------------------------------------------------------------------- the rule

    /// <summary>
    /// Balanced quotes are removed wherever they sit in the word — POSIX word expansion's
    /// rule, and the one every borrowed shell line already assumes.
    /// </summary>
    [Theory]
    [InlineData("--opt=\"x\"", "--opt=x")]
    [InlineData("--opt='x'", "--opt=x")]
    [InlineData("--opt=\"a\"b\"c\"", "--opt=abc")]
    [InlineData("--include=\"*.cs\"", "--include=*.cs")]
    [InlineData("plain", "plain")]
    public void Balanced_quotes_are_removed(string word, string expected)
        => Assert.Equal(expected, ShellWordQuoting.StripBalancedQuotes(word));

    /// <summary>
    /// An **unbalanced** quote leaves the word exactly as written. `5"` is inches, and
    /// silently deleting the mark would be a second silent-wrong-answer in the code built
    /// to remove one.
    /// </summary>
    [Theory]
    [InlineData("5\"")]
    [InlineData("it's")]
    [InlineData("--opt=\"a")]
    public void An_unbalanced_quote_leaves_the_word_alone(string word)
        => Assert.Equal(word, ShellWordQuoting.StripBalancedQuotes(word));

    /// <summary>
    /// The other quote character is literal inside a quoted run, which is what makes
    /// `'"'` a spelling for a literal double quote.
    /// </summary>
    [Fact]
    public void A_quote_inside_the_other_kind_is_literal()
    {
        Assert.Equal("\"", ShellWordQuoting.StripBalancedQuotes("'\"'"));
        Assert.Equal("'", ShellWordQuoting.StripBalancedQuotes("\"'\""));
    }

    // ------------------------------------------------------------ through the engine

    /// <summary>
    /// The defect as filed, end to end.
    /// </summary>
    [Theory]
    [InlineData("echo --opt=\"x\"", "--opt=x")]
    [InlineData("echo --opt='x'", "--opt=x")]
    [InlineData("echo --opt=\"a\"b\"c\"", "--opt=abc")]
    [InlineData("echo plain=\"q\"", "plain=q")]
    public async Task A_quoted_word_reaches_a_command_unquoted(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A word that already began with a quote is untouched — it always worked, so the fix
    /// must not reach it.
    /// </summary>
    [Fact]
    public async Task A_leading_quoted_word_is_unchanged()
        => Assert.Equal("--opt=x", await RunAsync("echo \"--opt=x\""));

    /// <summary>
    /// Quoting still protects a space, which is the half a quote-removal-only fix would
    /// have missed: `--opt="a b"` was **two** arguments, `--opt="a` and `b"`.
    /// </summary>
    [Fact]
    public async Task Quoting_protects_an_embedded_space()
        => Assert.Equal("--opt=a b", await RunAsync("echo --opt=\"a b\""));

    /// <summary>
    /// And quoting still suppresses glob expansion. This is why the glob step checks the
    /// *written* form rather than the value: by the time it runs, `x"*"y` and `x*y` are the
    /// same string, and only the syntax still knows which one asked for expansion.
    /// </summary>
    [Fact]
    public async Task Quoting_still_suppresses_glob_expansion()
        => Assert.Equal("x*y", await RunAsync("echo x\"*\"y"));

    /// <summary>
    /// An apostrophe stays an apostrophe. The lexer only treats a quote as opening a run
    /// when its partner is on the same line — without that, `don't` would swallow the rest
    /// of the line, which is a worse defect than the one being fixed and lands on words
    /// people type at a prompt.
    /// </summary>
    [Theory]
    [InlineData("echo it's fine", "it's|fine")]
    [InlineData("echo 5\" pipe", "5\"|pipe")]
    [InlineData("echo don't stop now", "don't|stop|now")]
    public async Task An_unpartnered_quote_does_not_swallow_the_line(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}

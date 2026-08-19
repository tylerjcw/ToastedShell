namespace Tosh.Runtime;

/// <summary>
/// Removes the quote characters from a word that carries them inside itself.
/// </summary>
/// <remarks>
/// <para>
/// `TOSH-0001`. A word beginning with a quote is lexed as a string and arrives unquoted,
/// but a word that merely *contains* one — <c>--opt="x"</c> — is lexed whole and kept its
/// quote characters all the way to the callee. So <c>grep -roh --include="*.cs"</c> searched
/// for files literally named <c>"*.cs"</c>, quotes included, and reported zero matches where
/// bash reports 854. Nothing errored: the command succeeded and found nothing, which is the
/// worst shape this kind of defect can take.
/// </para>
/// <para>
/// The rule is POSIX word expansion's: quote characters are removed wherever they are
/// **balanced within a single word**. <c>--opt="a"b"c"</c> is <c>--opt=abc</c>, because
/// three quoted and unquoted runs concatenate into one word — the reading the item asked to
/// be decided deliberately, and the one every borrowed shell line already assumes.
/// </para>
/// <para>
/// An **unbalanced** quote leaves the word untouched rather than dropping the stray
/// character. A word like <c>5"</c> is inches, and a rule that silently deleted the mark
/// would be a second silent-wrong-answer in the place built to remove one.
/// </para>
/// <para>
/// A literal quote is spelled by quoting the whole word — <c>"--opt=\"x\""</c> — or with the
/// other quote character, <c>'"'</c>. Both already worked and are unaffected.
/// </para>
/// </remarks>
public static class ShellWordQuoting
{
    /// <summary>
    /// Whether <paramref name="word"/> carries quote characters that
    /// <see cref="StripBalancedQuotes"/> would act on.
    /// </summary>
    /// <remarks>
    /// Used to keep quoting's *other* job intact. Glob expansion runs on a word's evaluated
    /// text, so stripping alone would turn <c>x"*"y</c> — which suppresses expansion today —
    /// back into a live pattern. A caller that expands globs asks this first.
    /// </remarks>
    public static bool ContainsQuote(string? word)
        => word is not null && (word.Contains('"') || word.Contains('\''));

    /// <summary>
    /// Returns <paramref name="word"/> with balanced quote characters removed.
    /// </summary>
    public static string StripBalancedQuotes(string word)
    {
        if (!ContainsQuote(word))
        {
            return word;
        }

        var builder = new System.Text.StringBuilder(word.Length);
        var quote = '\0';

        foreach (var character in word)
        {
            if (quote == '\0' && character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (quote != '\0' && character == quote)
            {
                quote = '\0';
                continue;
            }

            builder.Append(character);
        }

        // Unbalanced: a quote was opened and never closed, so the word is left exactly as
        // written rather than half-stripped.
        return quote == '\0' ? builder.ToString() : word;
    }
}

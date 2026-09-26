namespace Tosh.Runtime;

internal static class ShellCommandLineEscaper
{
    public static string Quote(object? value)
    {
        var text = ExternalTextSerializer.Serialize(value);

        if (text.Length == 0)
        {
            return "\"\"";
        }

        if (text.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '-' or '_' or '.' or '/' or ':' or '+' or '=' or '@'))
        {
            return text;
        }

        return QuoteText(text);
    }

    /// <summary>
    /// Quotes <paramref name="value"/> whatever it contains, so that re-parsed it is a value and
    /// never a word — never an option (<c>TOSH-0013</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="Quote"/> leaves <c>-rf</c> bare because it needs no quoting to survive the
    /// lexer; re-parsed, a bare <c>-rf</c> is an option. Anything that did not come from a word
    /// the user wrote — a line of input, a value from a variable — goes through here instead.
    /// </remarks>
    public static string QuoteValue(object? value) => QuoteText(ExternalTextSerializer.Serialize(value));

    private static string QuoteText(string text)
        => "\"" + text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

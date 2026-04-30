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

        return "\"" + text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

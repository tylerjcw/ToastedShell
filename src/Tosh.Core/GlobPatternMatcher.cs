using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Tosh.Core;

internal static class GlobPatternMatcher
{
    private static readonly ConcurrentDictionary<(string Pattern, bool IgnoreCase), Regex> Cache = new();

    public static bool IsMatch(string text, string pattern, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var regex = Cache.GetOrAdd((pattern, ignoreCase), static key => BuildRegex(key.Pattern, key.IgnoreCase));
        return regex.IsMatch(text);
    }

    private static Regex BuildRegex(string pattern, bool ignoreCase)
    {
        var builder = new StringBuilder("^");

        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];

            if (character == '[')
            {
                var closeIndex = pattern.IndexOf(']', index + 1);

                if (closeIndex <= index + 1)
                {
                    builder.Append(@"\[");
                    continue;
                }

                builder.Append(BuildCharacterClass(pattern[(index + 1)..closeIndex]));
                index = closeIndex;
                continue;
            }

            builder.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString()),
            });
        }

        builder.Append('$');

        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(builder.ToString(), options);
    }

    private static string BuildCharacterClass(string contents)
    {
        var builder = new StringBuilder("[");
        var startIndex = 0;

        if (contents.Length > 0 && contents[0] is '!' or '^')
        {
            builder.Append('^');
            startIndex = 1;
        }

        for (var index = startIndex; index < contents.Length; index++)
        {
            var character = contents[index];

            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case ']':
                    builder.Append(@"\]");
                    break;
                case '^' when index == startIndex:
                    builder.Append(@"\^");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append(']');
        return builder.ToString();
    }
}

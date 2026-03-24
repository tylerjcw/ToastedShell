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

        foreach (var character in pattern)
        {
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
}

namespace Tosh.Core;

// Adapted from the fuzzy matcher in GondolinLib.Search so Tosh can use the
// same matching behavior without taking on a cross-repo dependency yet.
internal static class HelpFuzzyMatcher
{
    public static int EditDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a))
        {
            return b?.Length ?? 0;
        }

        if (string.IsNullOrEmpty(b))
        {
            return a.Length;
        }

        var la = a.Length;
        var lb = b.Length;
        var d = new int[la + 1, lb + 1];

        for (var i = 0; i <= la; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= lb; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= la; i++)
        {
            for (var j = 1; j <= lb; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
                }
            }
        }

        return d[la, lb];
    }

    public static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
        {
            return 1.0;
        }

        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0.0;
        }

        var maxLength = Math.Max(a.Length, b.Length);
        var distance = EditDistance(a.ToLowerInvariant(), b.ToLowerInvariant());
        return 1.0 - ((double)distance / maxLength);
    }

    public static bool IsSimilar(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return false;
        }

        var textLower = text.ToLowerInvariant();
        var queryLower = query.ToLowerInvariant();

        if (textLower == queryLower || textLower.Contains(queryLower, StringComparison.Ordinal))
        {
            return true;
        }

        var distance = EditDistance(textLower, queryLower);
        var maxLength = Math.Max(text.Length, query.Length);

        if (maxLength <= 5)
        {
            return distance <= 1;
        }

        if (maxLength <= 8)
        {
            return distance <= 2;
        }

        return distance <= (int)Math.Ceiling(maxLength * 0.25);
    }

    public static double FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return 0.0;
        }

        var textLower = text.ToLowerInvariant();
        var queryLower = query.ToLowerInvariant();

        if (textLower == queryLower)
        {
            return 1.0;
        }

        if (textLower.Contains(queryLower, StringComparison.Ordinal))
        {
            return 0.9;
        }

        var words = textLower.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (IsSimilar(word, queryLower))
            {
                return Similarity(word, queryLower) * 0.8;
            }
        }

        foreach (var word in words)
        {
            if (word.Length < 2 || queryLower.Length < 2)
            {
                continue;
            }

            var minLength = Math.Min(word.Length, queryLower.Length);

            if (IsSimilar(word[..minLength], queryLower[..minLength]))
            {
                return Similarity(word[..minLength], queryLower[..minLength]) * 0.6;
            }
        }

        if (IsSimilar(textLower, queryLower))
        {
            return Similarity(textLower, queryLower) * 0.7;
        }

        return 0.0;
    }

    public static double FuzzyMatchLower(string textLower, string queryLower)
    {
        if (string.IsNullOrEmpty(textLower) || string.IsNullOrEmpty(queryLower))
        {
            return 0.0;
        }

        if (textLower == queryLower)
        {
            return 1.0;
        }

        if (textLower.Contains(queryLower, StringComparison.Ordinal))
        {
            return 0.9;
        }

        var words = textLower.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (IsSimilar(word, queryLower))
            {
                return Similarity(word, queryLower) * 0.8;
            }
        }

        foreach (var word in words)
        {
            if (word.Length < 2 || queryLower.Length < 2)
            {
                continue;
            }

            var minLength = Math.Min(word.Length, queryLower.Length);

            if (IsSimilar(word[..minLength], queryLower[..minLength]))
            {
                return Similarity(word[..minLength], queryLower[..minLength]) * 0.6;
            }
        }

        if (IsSimilar(textLower, queryLower))
        {
            return Similarity(textLower, queryLower) * 0.7;
        }

        return 0.0;
    }

}

using System.Globalization;
using System.Text;

namespace Tosh.Tui.Rendering;

/// <summary>
/// How many terminal columns a piece of text occupies.
/// </summary>
/// <remarks>
/// <para>
/// This replaces counting UTF-16 code units, which is what the TUI did everywhere and
/// which is right only for ASCII (<c>TUI-0005</c>). Three separate things make a
/// character's width differ from its length in code units:
/// </para>
/// <list type="bullet">
///   <item>Wide and Fullwidth characters — most CJK — occupy two columns each.</item>
///   <item>Combining marks, joiners and variation selectors occupy none: they modify
///         the character before them rather than standing on their own.</item>
///   <item>A visible character may be several code units. An emoji is a surrogate pair;
///         a family emoji is several emoji joined by zero-width joiners, and the whole
///         sequence is one thing on screen.</item>
/// </list>
/// <para>
/// So the unit of measurement is the grapheme cluster — what a user would call a
/// character — and its width is decided by the character it starts with.
/// </para>
/// </remarks>
public static class TuiTextMeasure
{
    /// <summary>Columns occupied by <paramref name="text"/>.</summary>
    public static int MeasureWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var total = 0;

        foreach (var cluster in EnumerateClusters(text))
        {
            total += ClusterWidth(cluster);
        }

        return total;
    }

    /// <summary>Splits text into grapheme clusters — one visible character each.</summary>
    public static IEnumerable<string> EnumerateClusters(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }

    /// <summary>Columns occupied by one grapheme cluster: 0, 1 or 2.</summary>
    public static int ClusterWidth(string cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (cluster.Length == 0)
        {
            return 0;
        }

        // An emoji presentation selector makes the preceding character render as a
        // full-width emoji even when its own code point is narrow — U+2764 is one column
        // as a dingbat and two as ❤️.
        if (cluster.Contains('️'))
        {
            return 2;
        }

        var first = char.ConvertToUtf32(cluster, 0);

        if (IsZeroWidth(first))
        {
            return 0;
        }

        return IsWide(first) ? 2 : 1;
    }

    /// <summary>
    /// Truncates to a column budget without splitting a cluster or leaving half of a
    /// wide character behind.
    /// </summary>
    /// <remarks>
    /// When a two-column character straddles the limit it is dropped rather than
    /// half-drawn; the caller gets text one column short, which is the only honest answer
    /// a cell grid can give.
    /// </remarks>
    public static string Truncate(string text, int maxColumns)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxColumns <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var used = 0;

        foreach (var cluster in EnumerateClusters(text))
        {
            var width = ClusterWidth(cluster);

            if (used + width > maxColumns)
            {
                break;
            }

            builder.Append(cluster);
            used += width;
        }

        return builder.ToString();
    }

    private static bool IsZeroWidth(int codePoint)
    {
        // Zero-width joiner and the variation selectors, which modify their neighbour.
        if (codePoint == 0x200B || codePoint == 0x200C || codePoint == 0x200D ||
            (codePoint >= 0xFE00 && codePoint <= 0xFE0F))
        {
            return true;
        }

        // C0 and C1 controls draw nothing. A terminal's own escape handling deals with
        // the ones that mean something.
        if (codePoint < 0x20 || (codePoint >= 0x7F && codePoint <= 0x9F))
        {
            return true;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0);

        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.Format;
    }

    /// <summary>
    /// The East Asian Wide and Fullwidth ranges, plus the emoji blocks that terminals
    /// render double-width.
    /// </summary>
    /// <remarks>
    /// A table rather than a lookup because .NET exposes no East Asian width property.
    /// It follows Unicode's <c>EastAsianWidth.txt</c> for the W and F classes and needs
    /// revisiting when a Unicode release adds a block — which is why it is one sorted
    /// array in one place rather than conditions spread through the renderer.
    /// </remarks>
    private static readonly (int Start, int End)[] WideRanges =
    [
        (0x1100, 0x115F),   // Hangul Jamo initial consonants
        (0x2329, 0x232A),   // angle brackets
        (0x2E80, 0x303E),   // CJK radicals, Kangxi, CJK symbols
        (0x3041, 0x33FF),   // kana, Hangul compatibility jamo, CJK compatibility
        (0x3400, 0x4DBF),   // CJK extension A
        (0x4E00, 0x9FFF),   // CJK unified ideographs
        (0xA000, 0xA4CF),   // Yi
        (0xA960, 0xA97F),   // Hangul Jamo extended-A
        (0xAC00, 0xD7A3),   // Hangul syllables
        (0xF900, 0xFAFF),   // CJK compatibility ideographs
        (0xFE10, 0xFE19),   // vertical forms
        (0xFE30, 0xFE6F),   // CJK compatibility forms
        (0xFF00, 0xFF60),   // fullwidth forms
        (0xFFE0, 0xFFE6),   // fullwidth signs
        (0x1B000, 0x1B001), // kana supplement
        (0x1F004, 0x1F004), // mahjong red dragon
        (0x1F0CF, 0x1F0CF), // playing card black joker
        (0x1F18E, 0x1F18E), // negative squared AB
        (0x1F191, 0x1F19A), // squared CL to squared VS
        (0x1F200, 0x1F320), // enclosed ideographic supplement, misc symbols
        (0x1F330, 0x1F335),
        (0x1F337, 0x1F37C),
        (0x1F380, 0x1F393),
        (0x1F3A0, 0x1F3CA),
        (0x1F3E0, 0x1F3F0),
        (0x1F400, 0x1F4FC),
        (0x1F500, 0x1F53D),
        (0x1F550, 0x1F567),
        (0x1F600, 0x1F64F), // emoticons
        (0x1F680, 0x1F6C5), // transport and map symbols
        (0x1F900, 0x1F9FF), // supplemental symbols and pictographs
        (0x20000, 0x2FFFD), // CJK extension B and beyond
        (0x30000, 0x3FFFD),
    ];

    private static bool IsWide(int codePoint)
    {
        var low = 0;
        var high = WideRanges.Length - 1;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            var (start, end) = WideRanges[middle];

            if (codePoint < start)
            {
                high = middle - 1;
            }
            else if (codePoint > end)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}

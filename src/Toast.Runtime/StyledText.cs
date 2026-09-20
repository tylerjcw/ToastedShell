using System.Text.RegularExpressions;

namespace Tosh.Runtime;

public sealed record StyledText(
    string Text,
    string? Foreground = null,
    string? Background = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Dim = false,
    string? Link = null)
{
    private static readonly Regex AnsiSequenceRegex = new(@"\x1B\[[0-9;]*m|\x1B]8;;.*?(?:\x1B\\|\x07)", RegexOptions.Compiled);

    public override string ToString() => Text;

    private static readonly Dictionary<string, string> ForegroundCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "30",
        ["red"] = "31",
        ["green"] = "32",
        ["yellow"] = "33",
        ["blue"] = "34",
        ["magenta"] = "35",
        ["cyan"] = "36",
        ["white"] = "37",
        ["gray"] = "90",
        ["grey"] = "90",
        ["bright-red"] = "91",
        ["bright-green"] = "92",
        ["bright-yellow"] = "93",
        ["bright-blue"] = "94",
        ["bright-magenta"] = "95",
        ["bright-cyan"] = "96",
        ["bright-white"] = "97",
    };

    private static readonly Dictionary<string, string> BackgroundCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "40",
        ["red"] = "41",
        ["green"] = "42",
        ["yellow"] = "43",
        ["blue"] = "44",
        ["magenta"] = "45",
        ["cyan"] = "46",
        ["white"] = "47",
        ["gray"] = "100",
        ["grey"] = "100",
        ["bright-red"] = "101",
        ["bright-green"] = "102",
        ["bright-yellow"] = "103",
        ["bright-blue"] = "104",
        ["bright-magenta"] = "105",
        ["bright-cyan"] = "106",
        ["bright-white"] = "107",
    };

    public static IReadOnlyList<string> SupportedNamedColors { get; } =
    [
        "black",
        "red",
        "green",
        "yellow",
        "blue",
        "magenta",
        "cyan",
        "white",
        "gray",
        "bright-red",
        "bright-green",
        "bright-yellow",
        "bright-blue",
        "bright-magenta",
        "bright-cyan",
        "bright-white",
    ];

    public string ToAnsi()
    {
        if (Foreground is null && Background is null && !Bold && !Italic && !Underline && !Dim && Link is null)
        {
            return Text;
        }

        var introducer = BuildSgrIntroducer(Foreground, Background, Bold, Italic, Underline, Dim);

        var renderedText = introducer.Length == 0
            ? Text
            : $"{introducer}{Text}\x1b[0m";

        if (string.IsNullOrWhiteSpace(Link))
        {
            return renderedText;
        }

        return $"\x1b]8;;{Link}\x1b\\{renderedText}\x1b]8;;\x1b\\";
    }

    /// <summary>
    /// The SGR sequence that switches these attributes on, with no text and no reset.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="ToAnsi"/> for the TUI's cell renderer, which emits styling
    /// per run of cells rather than per string and so needs the introducer on its own.
    /// Keeping one implementation keeps one colour table: the named-colour and hex
    /// handling below is the only place either is understood.
    /// </remarks>
    public static string BuildSgrIntroducer(
        string? foreground,
        string? background,
        bool bold = false,
        bool italic = false,
        bool underline = false,
        bool dim = false,
        bool reverse = false)
    {
        var codes = new List<string>();

        if (bold) codes.Add("1");
        if (dim) codes.Add("2");
        if (italic) codes.Add("3");
        if (underline) codes.Add("4");
        if (reverse) codes.Add("7");

        if (foreground is not null)
        {
            if (ForegroundCodes.TryGetValue(foreground, out var foregroundCode))
            {
                codes.Add(foregroundCode);
            }
            else if (TryParseHexColor(foreground, out var r, out var g, out var b))
            {
                codes.Add($"38;2;{r};{g};{b}");
            }
        }

        if (background is not null)
        {
            if (BackgroundCodes.TryGetValue(background, out var backgroundCode))
            {
                codes.Add(backgroundCode);
            }
            else if (TryParseHexColor(background, out var r, out var g, out var b))
            {
                codes.Add($"48;2;{r};{g};{b}");
            }
        }

        return codes.Count == 0 ? string.Empty : $"\x1b[{string.Join(';', codes)}m";
    }

    public static bool IsSupportedColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return ForegroundCodes.ContainsKey(value) ||
               BackgroundCodes.ContainsKey(value) ||
               TryParseHexColor(value, out _, out _, out _);
    }

    private static bool TryParseHexColor(string value, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var hex = value.StartsWith('#') ? value[1..] : value;

        if (hex.Length == 6 &&
            int.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out r) &&
            int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out g) &&
            int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out b))
        {
            return true;
        }

        return false;
    }

    public static string RenderSegments(IEnumerable<object?> segments)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case StyledText styled:
                    builder.Append(styled.ToAnsi());
                    break;
                case string text:
                    builder.Append(text);
                    break;
                case not null:
                    builder.Append(segment.ToString());
                    break;
            }
        }

        return builder.ToString();
    }

    public static string StripAnsi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('\x1B') ? AnsiSequenceRegex.Replace(text, string.Empty) : text;
    }

    /// <summary>Columns <paramref name="text"/> occupies once its escapes are removed.</summary>
    /// <remarks>
    /// `TUI-0005`. This counted UTF-16 code units, which is the right answer for ASCII and
    /// wrong for everything else a terminal draws: `日本語` is three code units and six
    /// columns, so every caller padding to a width pushed the right border of its box out
    /// by three on exactly the rows with CJK in them. The TUI already measures properly and
    /// had the same bug fixed under `TUI-0012`; these callers kept the old answer because
    /// the measurement lived above them. It now lives here, so there is one of it.
    /// </remarks>
    public static int GetVisibleLength(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TextMeasure.MeasureWidth(StripAnsi(text));
    }
}

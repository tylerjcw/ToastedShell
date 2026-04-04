using System.Text.RegularExpressions;

namespace Tosh.Core;

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

        var codes = new List<string>();

        if (Bold) codes.Add("1");
        if (Dim) codes.Add("2");
        if (Italic) codes.Add("3");
        if (Underline) codes.Add("4");

        if (Foreground is not null)
        {
            if (ForegroundCodes.TryGetValue(Foreground, out var fgCode))
            {
                codes.Add(fgCode);
            }
            else if (TryParseHexColor(Foreground, out var r, out var g, out var b))
            {
                codes.Add($"38;2;{r};{g};{b}");
            }
        }

        if (Background is not null)
        {
            if (BackgroundCodes.TryGetValue(Background, out var bgCode))
            {
                codes.Add(bgCode);
            }
            else if (TryParseHexColor(Background, out var r, out var g, out var b))
            {
                codes.Add($"48;2;{r};{g};{b}");
            }
        }

        var renderedText = codes.Count == 0
            ? Text
            : $"\x1b[{string.Join(';', codes)}m{Text}\x1b[0m";

        if (string.IsNullOrWhiteSpace(Link))
        {
            return renderedText;
        }

        return $"\x1b]8;;{Link}\x1b\\{renderedText}\x1b]8;;\x1b\\";
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
        return AnsiSequenceRegex.Replace(text, string.Empty);
    }

    public static int GetVisibleLength(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return StripAnsi(text).Length;
    }
}

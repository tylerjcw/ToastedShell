using System.Globalization;
using System.Text.RegularExpressions;

namespace Tosh.Core;

public static partial class NetworkctlListParser
{
    public static IReadOnlyList<SystemdNetworkLinkInfo> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<SystemdNetworkLinkInfo>();
        }

        var rows = new List<SystemdNetworkLinkInfo>();

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith("IDX ", StringComparison.Ordinal) ||
                LinksListedRegex().IsMatch(line))
            {
                continue;
            }

            var match = LinkRowRegex().Match(line);

            if (!match.Success)
            {
                throw new InvalidOperationException($"Could not parse networkctl list row '{line}'.");
            }

            rows.Add(
                new SystemdNetworkLinkInfo(
                    Index: int.Parse(match.Groups["idx"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    Link: match.Groups["link"].Value,
                    Type: match.Groups["type"].Value,
                    OperationalState: match.Groups["oper"].Value,
                    SetupState: match.Groups["setup"].Value));
        }

        return rows;
    }

    [GeneratedRegex(@"^\s*(?<idx>\d+)\s+(?<link>\S+)\s+(?<type>\S+)\s+(?<oper>\S+)\s+(?<setup>\S+)\s*$", RegexOptions.Compiled)]
    private static partial Regex LinkRowRegex();

    [GeneratedRegex(@"^\s*\d+\s+links?\s+listed\.\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LinksListedRegex();
}

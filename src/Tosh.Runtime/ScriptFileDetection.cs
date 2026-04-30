using System.Text;

namespace Tosh.Runtime;

public static class ScriptFileDetection
{
    public static bool IsToshScript(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (string.Equals(Path.GetExtension(path), ".tosh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ReadShebang(path) is { IsTosh: true };
    }

    public static ScriptShebangInfo? ReadShebang(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var firstLine = reader.ReadLine();

        if (string.IsNullOrWhiteSpace(firstLine) || !firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            return null;
        }

        var raw = firstLine[2..].Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tokens = SplitShebang(raw);

        if (tokens.Count == 0)
        {
            return null;
        }

        if (IsEnvCommand(tokens[0]))
        {
            if (tokens.Count >= 3 && string.Equals(tokens[1], "-S", StringComparison.Ordinal))
            {
                tokens = tokens.Skip(2).ToList();
            }
            else
            {
                tokens = tokens.Skip(1).ToList();
            }
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        return new ScriptShebangInfo(tokens.ToArray(), IsToshCommand(tokens[0]));
    }

    private static List<string> SplitShebang(string raw)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        foreach (var character in raw)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool IsEnvCommand(string command)
    {
        var fileName = Path.GetFileNameWithoutExtension(command);
        return string.Equals(fileName, "env", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsToshCommand(string command)
    {
        var fileName = Path.GetFileNameWithoutExtension(command);
        return string.Equals(fileName, "tosh", StringComparison.OrdinalIgnoreCase);
    }
}

public readonly record struct ScriptShebangInfo(
    IReadOnlyList<string> CommandTokens,
    bool IsTosh);

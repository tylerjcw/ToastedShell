namespace Tosh.Cli;

public static class ReplInputClassifier
{
    public static bool RequiresContinuation(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var parenDepth = 0;
        var braceDepth = 0;
        var bracketDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaping = false;
        var inComment = false;
        char? lastSignificant = null;

        foreach (var line in lines)
        {
            foreach (var character in line)
            {
                if (inComment)
                {
                    continue;
                }

                if (inSingleQuote)
                {
                    if (escaping)
                    {
                        escaping = false;
                        continue;
                    }

                    if (character == '\\')
                    {
                        escaping = true;
                        continue;
                    }

                    if (character == '\'')
                    {
                        inSingleQuote = false;
                    }

                    continue;
                }

                if (inDoubleQuote)
                {
                    if (escaping)
                    {
                        escaping = false;
                        continue;
                    }

                    if (character == '\\')
                    {
                        escaping = true;
                        continue;
                    }

                    if (character == '"')
                    {
                        inDoubleQuote = false;
                    }

                    continue;
                }

                switch (character)
                {
                    case '#':
                        inComment = true;
                        break;

                    case '\'':
                        inSingleQuote = true;
                        break;

                    case '"':
                        inDoubleQuote = true;
                        break;

                    case '(':
                        parenDepth++;
                        lastSignificant = character;
                        break;

                    case ')':
                        parenDepth = Math.Max(0, parenDepth - 1);
                        lastSignificant = character;
                        break;

                    case '{':
                        braceDepth++;
                        lastSignificant = character;
                        break;

                    case '}':
                        braceDepth = Math.Max(0, braceDepth - 1);
                        lastSignificant = character;
                        break;

                    case '[':
                        bracketDepth++;
                        lastSignificant = character;
                        break;

                    case ']':
                        bracketDepth = Math.Max(0, bracketDepth - 1);
                        lastSignificant = character;
                        break;

                    default:
                        if (!char.IsWhiteSpace(character))
                        {
                            lastSignificant = character;
                        }
                        break;
                }
            }

            inComment = false;
        }

        return inSingleQuote ||
               inDoubleQuote ||
               parenDepth > 0 ||
               braceDepth > 0 ||
               bracketDepth > 0 ||
               lastSignificant == '|';
    }
}

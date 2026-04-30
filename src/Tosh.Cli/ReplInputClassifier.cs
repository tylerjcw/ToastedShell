using Tosh.Language.Parsing;

namespace Tosh.Cli;

public static class ReplInputClassifier
{
    private static readonly HashSet<string> CompoundKeywords =
    [
        "func", "if", "for", "while", "until", "try", "switch",
        "class", "struct", "trait", "module", "enum", "record", "event", "bind",
    ];

    private static readonly HashSet<string> ContinuationDiagnosticCodes =
    [
        "tosh.parser.expected_block",
        "tosh.parser.expected_class_body",
        "tosh.parser.expected_enum_body",
        "tosh.parser.expected_record_fields",
        "tosh.parser.expected_match_block",
        "tosh.parser.expected_switch_block",
        "tosh.parser.expected_bind_body",
        "tosh.parser.expected_function_signature",
        "tosh.parser.expected_else_block",
        "tosh.parser.missing_closing_brace",
        "tosh.parser.missing_closing_paren",
        "tosh.parser.missing_closing_parenthesis",
        "tosh.parser.missing_closing_bracket",
        "tosh.parser.missing_closing_angle",
        "tosh.parser.missing_projection_closing_brace",
        "tosh.parser.missing_record_closing_brace",
        "tosh.parser.missing_command_after_pipe",
        "tosh.parser.missing_ternary_colon",
    ];

    public static bool RequiresContinuation(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return GetContinuationState(source).RequiresContinuation;
    }

    public static string GetSuggestedContinuationText(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return GetContinuationState(source).SuggestedIndent;
    }

    public static ReplContinuationState GetContinuationState(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return GetContinuationState(SplitLines(source));
    }

    public static bool RequiresContinuation(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return GetContinuationState(lines).RequiresContinuation;
    }

    public static string GetSuggestedContinuationText(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return GetContinuationState(lines).SuggestedIndent;
    }

    public static ReplContinuationState GetContinuationState(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var analysis = Analyze(lines);

        if (!analysis.RequiresContinuation)
        {
            return new ReplContinuationState(false, string.Empty);
        }

        var baseIndent = analysis.LastLineIndent;

        if (analysis.ShouldIndentNextLine)
        {
            baseIndent += 4;
        }

        return new ReplContinuationState(true, new string(' ', Math.Max(0, baseIndent)));
    }

    private static ContinuationAnalysis Analyze(IReadOnlyList<string> lines)
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
        string lastNonEmptyLine = string.Empty;

        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lastNonEmptyLine = line;
            }

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

        var trimmedLastLine = lastNonEmptyLine.TrimEnd();
        var lastWord = trimmedLastLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var lastLineIndent = lastNonEmptyLine.TakeWhile(character => character is ' ' or '\t').Count();
        var trailingOperator = trimmedLastLine.EndsWith("|", StringComparison.Ordinal) ||
                               trimmedLastLine.EndsWith("=>", StringComparison.Ordinal) ||
                               trimmedLastLine.EndsWith("?", StringComparison.Ordinal) ||
                               trimmedLastLine.EndsWith(":", StringComparison.Ordinal) ||
                               trimmedLastLine.EndsWith("??", StringComparison.Ordinal) ||
                               string.Equals(lastWord, "and", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(lastWord, "or", StringComparison.OrdinalIgnoreCase);
        var trailingOpener = trimmedLastLine.EndsWith("{", StringComparison.Ordinal) ||
                             trimmedLastLine.EndsWith("(", StringComparison.Ordinal) ||
                             trimmedLastLine.EndsWith("[", StringComparison.Ordinal);
        var requiresContinuation = inSingleQuote ||
                                   inDoubleQuote ||
                                   parenDepth > 0 ||
                                   braceDepth > 0 ||
                                   bracketDepth > 0 ||
                                   lastSignificant == '|' ||
                                   trailingOperator;

        if (!requiresContinuation)
        {
            requiresContinuation = RequiresContinuationFromParser(lines, out var parserShouldIndent);

            if (requiresContinuation)
            {
                return new ContinuationAnalysis(
                    true,
                    lastLineIndent,
                    parserShouldIndent || trailingOpener);
            }
        }

        return new ContinuationAnalysis(
            requiresContinuation,
            lastLineIndent,
            trailingOperator || trailingOpener);
    }

    private static bool RequiresContinuationFromParser(IReadOnlyList<string> lines, out bool shouldIndent)
    {
        shouldIndent = false;

        var source = string.Join('\n', lines);
        var trimmed = source.Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        // Quick keyword gate: only invoke the parser for input that starts
        // with a compound statement keyword (possibly preceded by a modifier).
        if (!StartsWithCompoundKeyword(trimmed))
        {
            return false;
        }

        try
        {
            var result = ToshParser.Parse(source, "<repl-continuation>");

            foreach (var diagnostic in result.Diagnostics)
            {
                if (ContinuationDiagnosticCodes.Contains(diagnostic.Code))
                {
                    // Suggest indentation when the parser is expecting a block body.
                    shouldIndent = diagnostic.Code is "tosh.parser.expected_block"
                        or "tosh.parser.expected_class_body"
                        or "tosh.parser.expected_enum_body"
                        or "tosh.parser.expected_record_fields"
                        or "tosh.parser.expected_match_block"
                        or "tosh.parser.expected_switch_block"
                        or "tosh.parser.expected_bind_body"
                        or "tosh.parser.expected_function_signature";

                    return true;
                }
            }
        }
        catch
        {
            // If the parser throws (e.g. lexer diagnostic), fall through.
        }

        return false;
    }

    private static bool StartsWithCompoundKeyword(string trimmed)
    {
        // Extract the first word, skipping optional declaration modifiers
        // like "shy", "global", "export".
        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < Math.Min(words.Length, 3); i++)
        {
            var word = words[i];

            if (CompoundKeywords.Contains(word))
            {
                return true;
            }

            // Skip declaration modifiers that can precede compound keywords.
            if (word is not ("shy" or "global" or "export" or "local" or "required"
                or "sealed" or "hollow" or "hermit" or "strict" or "partial" or "fluid"))
            {
                break;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SplitLines(string source)
    {
        return source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private readonly record struct ContinuationAnalysis(bool RequiresContinuation, int LastLineIndent, bool ShouldIndentNextLine);
}

public readonly record struct ReplContinuationState(bool RequiresContinuation, string SuggestedIndent);

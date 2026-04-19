using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Cli;

public static class SyntaxHighlighter
{
    private static readonly ToshSyntaxThemeConfig DefaultTheme = new();

    private static readonly HashSet<string> PathFirstCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd",
        "ls",
        "cat",
        "mkdir",
        "touch",
        "rm",
        "cp",
        "mv",
        "find",
        "findmnt",
        "du",
        "df",
        "lsblk",
        "head",
        "tail",
        "grep",
        "open",
        "hash",
        "dirname",
        "basename",
        "readlink",
        "realpath",
        "archive",
        "extract",
        "tree",
        "trash",
        "source",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "var", "alloc", "func", "class", "struct", "trait", "module", "enum", "record", "prop", "shy", "static", "global", "export", "return", "throw", "if", "else", "for", "in",
        "while", "until", "break", "continue", "using", "require", "native", "bind", "from", "as", "out", "ref", "callconv", "try", "catch", "finally", "switch", "case", "default", "match",
        "shared", "sealed", "hollow", "fixed", "vital", "guarded", "overrule", "hermit", "strict", "lazy", "fading", "local", "raw", "partial", "proud", "public", "fluid", "fulfills", "uses",
    };

    private static readonly HashSet<string> LanguageForms = new(StringComparer.Ordinal)
    {
        "new", "nameof", "name-of",
    };

    private static readonly HashSet<string> OperatorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "is", "is-not", "in", "not-in",
    };

    private static readonly HashSet<string> BuiltInConstants = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null",
    };

    public static string Highlight(string input, ToshRuntime? runtime = null)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        IReadOnlyList<SyntaxToken> tokens;
        try
        {
            tokens = new ToshLexer(input).Lex();
        }
        catch
        {
            return input;
        }

        var result = new System.Text.StringBuilder(input.Length + tokens.Count * 10);
        var lastEnd = 0;

        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxTokenKind.EndOfFile)
            {
                break;
            }

            if (token.Span.Start > lastEnd)
            {
                var gap = input[lastEnd..token.Span.Start];
                var commentIndex = gap.IndexOf('#');

                if (commentIndex >= 0)
                {
                    result.Append(gap[..commentIndex]);
                    result.Append(GetTheme(runtime).Comment.Apply(gap[commentIndex..]).ToAnsi());
                }
                else
                {
                    result.Append(gap);
                }
            }

            if (token.Kind == SyntaxTokenKind.Bareword &&
                TryAppendDottedTypeHighlight(result, input, token, runtime))
            {
                lastEnd = token.Span.End;
                continue;
            }

            var style = GetTokenStyle(input, token, runtime);

            if (style is not null)
            {
                result.Append(style.Apply(input[token.Span.Start..token.Span.End]).ToAnsi());
            }
            else
            {
                result.Append(input[token.Span.Start..token.Span.End]);
            }

            lastEnd = token.Span.End;
        }

        if (lastEnd < input.Length)
        {
            result.Append(input[lastEnd..]);
        }

        return result.ToString();
    }

    private static ToshTextStyleConfig? GetTokenStyle(string input, SyntaxToken token, ToshRuntime? runtime)
    {
        var theme = GetTheme(runtime);

        return token.Kind switch
        {
            SyntaxTokenKind.String or SyntaxTokenKind.InterpolatedString => theme.String,
            SyntaxTokenKind.Number => theme.Number,
            SyntaxTokenKind.Boolean or SyntaxTokenKind.Null => theme.Constant,
            SyntaxTokenKind.Pipe or SyntaxTokenKind.Ampersand => theme.Operator,
            SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                or SyntaxTokenKind.Bang => theme.Operator,
            SyntaxTokenKind.GreaterThanGreaterThan
                or SyntaxTokenKind.LessThanLessThanLessThan => theme.Operator,
            SyntaxTokenKind.DollarOpenParen
                or SyntaxTokenKind.LessThanOpenParen => theme.Subexpression,
            SyntaxTokenKind.QuestionQuestion
                or SyntaxTokenKind.QuestionDot => theme.Subexpression,
            SyntaxTokenKind.OpenParen or SyntaxTokenKind.CloseParen
                or SyntaxTokenKind.OpenBrace or SyntaxTokenKind.CloseBrace
                or SyntaxTokenKind.OpenBracket or SyntaxTokenKind.CloseBracket
                or SyntaxTokenKind.Comma or SyntaxTokenKind.Semicolon => theme.Punctuation,
            SyntaxTokenKind.Bareword => GetBarewordStyle(input, token, runtime),
            _ => null,
        };
    }

    private static ToshTextStyleConfig? GetBarewordStyle(string input, SyntaxToken token, ToshRuntime? runtime)
    {
        var text = token.Text;
        var theme = GetTheme(runtime);

        if (Keywords.Contains(text))
        {
            return theme.Keyword;
        }

        if (LanguageForms.Contains(text))
        {
            return theme.LanguageForm;
        }

        if (OperatorWords.Contains(text) || text is "?" or ":")
        {
            return theme.Operator;
        }

        if (BuiltInConstants.Contains(text))
        {
            return theme.Constant;
        }

        if (IntrinsicLiteralParser.TryParseExpressionLiteral(text, out _))
        {
            return theme.Constant;
        }

        if (text.StartsWith("$", StringComparison.Ordinal) || text == "_")
        {
            return theme.Variable;
        }

        if (text.StartsWith("-", StringComparison.Ordinal))
        {
            return theme.Flag;
        }

        if (runtime is null)
        {
            return LooksLikeTypeOrNamespace(text) ? theme.Type : theme.Argument;
        }

        if (IsExistingPath(runtime, text, input, token.Span.Start))
        {
            return theme.Path;
        }

        if (IsCommandPosition(input, token.Span.Start))
        {
            return IsKnownCommand(runtime, text) ? theme.ValidCommand : theme.InvalidCommand;
        }

        if (LooksLikeTypeContext(input, token.Span.Start) && IsKnownTypeOrNamespace(runtime, text))
        {
            return theme.Type;
        }

        return theme.Argument;
    }

    private static ToshSyntaxThemeConfig GetTheme(ToshRuntime? runtime)
    {
        return runtime?.Config.Theme.Syntax ?? DefaultTheme;
    }

    private static bool IsKnownCommand(ToshRuntime runtime, string text)
    {
        if (runtime.Commands.TryGet(text, out _))
        {
            return true;
        }

        var external = ExternalCommandResolver.Resolve(runtime.CurrentDirectory, text);
        return external.Status == ExternalCommandLookupStatus.Found;
    }

    private static bool IsKnownTypeOrNamespace(ToshRuntime runtime, string text)
    {
        if (runtime.Classes.ContainsKey(text))
        {
            return true;
        }

        if (runtime.TypeResolver.Resolve(text) is not null)
        {
            return true;
        }

        if (runtime.TypeResolver is DotNetTypeResolver resolver)
        {
            if (resolver.GetAliases().ContainsKey(text))
            {
                return true;
            }

            if (resolver.GetImports().Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ReplClrCompletionCatalog.Shared.NamespaceExists(text);
    }

    private static bool IsExistingPath(ToshRuntime runtime, string text, string input, int tokenStart)
    {
        if (!ShouldTreatAsPathContext(input, tokenStart, text))
        {
            return false;
        }

        try
        {
            var resolvedPath = PathUtilities.ResolvePath(runtime.CurrentDirectory, text);
            return Directory.Exists(resolvedPath) || File.Exists(resolvedPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldTreatAsPathContext(string input, int tokenStart, string tokenText)
    {
        if (tokenText.StartsWith("$", StringComparison.Ordinal))
        {
            return false;
        }

        if (LooksLikePath(tokenText))
        {
            return true;
        }

        var segmentPrefix = GetCurrentSegmentPrefix(input, tokenStart);
        var tokens = SplitSegmentTokens(segmentPrefix);

        if (tokens.Count == 0)
        {
            return false;
        }

        var commandName = tokens[0];

        if (string.Equals(commandName, "require", StringComparison.OrdinalIgnoreCase))
        {
            return tokens.Count == 1 || string.Equals(tokens[^1], "from", StringComparison.OrdinalIgnoreCase);
        }

        return PathFirstCommands.Contains(commandName);
    }

    private static bool LooksLikeTypeContext(string input, int tokenStart)
    {
        var segmentPrefix = GetCurrentSegmentPrefix(input, tokenStart);
        var trimmed = segmentPrefix.TrimStart();

        return trimmed.StartsWith("using ", StringComparison.Ordinal) ||
               trimmed.StartsWith("new ", StringComparison.Ordinal) ||
               trimmed.Contains(" cast ", StringComparison.Ordinal);
    }

    private static bool IsCommandPosition(string input, int tokenStart)
    {
        var segmentPrefix = GetCurrentSegmentPrefix(input, tokenStart);
        return SplitSegmentTokens(segmentPrefix).Count == 0;
    }

    private static string GetCurrentSegmentPrefix(string input, int tokenStart)
    {
        var segmentStart = tokenStart;

        while (segmentStart > 0)
        {
            var character = input[segmentStart - 1];

            if (character is '|' or ';' or '&' or '\n' or '\r' or '{' or '}')
            {
                break;
            }

            segmentStart--;
        }

        return input[segmentStart..tokenStart].TrimStart();
    }

    private static IReadOnlyList<string> SplitSegmentTokens(string segmentPrefix)
    {
        return segmentPrefix
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool LooksLikePath(string tokenText)
    {
        if (tokenText.StartsWith("./", StringComparison.Ordinal) ||
            tokenText.StartsWith(".\\", StringComparison.Ordinal) ||
            tokenText.StartsWith("../", StringComparison.Ordinal) ||
            tokenText.StartsWith("..\\", StringComparison.Ordinal) ||
            tokenText.StartsWith("~/", StringComparison.Ordinal) ||
            tokenText.StartsWith("~\\", StringComparison.Ordinal) ||
            tokenText.StartsWith("/", StringComparison.Ordinal) ||
            tokenText.StartsWith("\\", StringComparison.Ordinal) ||
            tokenText.Contains(Path.DirectorySeparatorChar) ||
            tokenText.Contains(Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        if (tokenText.StartsWith('~') && tokenText.Length > 1 && PathUtilities.DirectoryAliases is not null)
        {
            var aliasName = tokenText[1..];
            return PathUtilities.DirectoryAliases.Aliases.ContainsKey(aliasName);
        }

        return false;
    }

    private static bool TryAppendDottedTypeHighlight(
        System.Text.StringBuilder result,
        string input,
        SyntaxToken token,
        ToshRuntime? runtime)
    {
        var text = token.Text;

        if (runtime is null || !text.Contains('.'))
        {
            return false;
        }

        var segments = text.Split('.');

        if (segments.Length < 2 || segments.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        // Walk from the left, accumulating namespace segments.
        // Stop when we find a segment that resolves as a type.
        var catalog = ReplClrCompletionCatalog.Shared;
        var theme = GetTheme(runtime);
        var namespaceParts = new List<string>();
        var typeIndex = -1;

        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = string.Join('.', segments[..(i + 1)]);

            if (catalog.NamespaceExists(candidate))
            {
                namespaceParts.Add(segments[i]);
                continue;
            }

            // Check if this segment is a type in the accumulated namespace.
            var namespacePath = string.Join('.', namespaceParts);

            if (namespaceParts.Count > 0 && runtime.TypeResolver.Resolve(candidate) is not null)
            {
                typeIndex = i;
                break;
            }

            // Also check using aliases (e.g., "IO" → "System.IO").
            if (runtime.TypeResolver is DotNetTypeResolver resolver)
            {
                var aliases = resolver.GetAliases();

                if (i == 0 && aliases.TryGetValue(segments[0], out var aliasNamespace))
                {
                    var remaining = string.Join('.', segments[1..]);

                    if (!string.IsNullOrEmpty(remaining))
                    {
                        var fullName = $"{aliasNamespace}.{remaining}";
                        var resolvedType = runtime.TypeResolver.Resolve(fullName);

                        if (resolvedType is not null || catalog.NamespaceExists($"{aliasNamespace}.{segments[1]}"))
                        {
                            // Color the alias as namespace, then recurse on the rest
                            namespaceParts.Add(segments[0]);
                            continue;
                        }
                    }
                }
            }

            // Not a namespace, not a recognized type — bail out.
            break;
        }

        if (typeIndex < 0 && namespaceParts.Count > 0 && namespaceParts.Count < segments.Length)
        {
            // The segment right after the last namespace might be a type even if
            // Resolve didn't find the fully-qualified name (e.g., user-defined module types).
            typeIndex = namespaceParts.Count;
        }

        if (typeIndex < 0 || namespaceParts.Count == 0)
        {
            return false;
        }

        // Emit: namespace segments in Namespace style, dots in Punctuation, type+rest in Type style.
        for (var i = 0; i < namespaceParts.Count; i++)
        {
            if (i > 0)
            {
                result.Append(theme.Punctuation.Apply(".").ToAnsi());
            }

            result.Append(theme.Namespace.Apply(namespaceParts[i]).ToAnsi());
        }

        // Dot before type
        result.Append(theme.Punctuation.Apply(".").ToAnsi());

        // Type name and any remaining segments (e.g., static members like Color.Green)
        var remaining2 = string.Join('.', segments[typeIndex..]);
        result.Append(theme.Type.Apply(remaining2).ToAnsi());

        return true;
    }

    private static bool LooksLikeTypeOrNamespace(string text)
    {
        return text.Contains('.', StringComparison.Ordinal) ||
               (text.Length > 0 && char.IsUpper(text[0]));
    }
}

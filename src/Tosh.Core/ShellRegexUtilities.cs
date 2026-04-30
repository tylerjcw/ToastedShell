using System.Text.RegularExpressions;

namespace Tosh.Core;

public static class ShellRegexUtilities
{
    private static readonly string[] RegexModifierFlags =
    [
        "i",
        "ignore-case",
        "m",
        "multiline",
        "s",
        "singleline",
        "dotall",
        "x",
        "ignore-pattern-whitespace",
        "explicit-capture",
    ];

    private static readonly string[] RegexOnlyModifierFlags =
    [
        "m",
        "multiline",
        "s",
        "singleline",
        "dotall",
        "x",
        "ignore-pattern-whitespace",
        "explicit-capture",
    ];

    public static bool HasModifierFlags(ParsedCommandArguments parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        return parsed.HasFlag(RegexModifierFlags);
    }

    public static bool HasRegexOnlyModifierFlags(ParsedCommandArguments parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        return parsed.HasFlag(RegexOnlyModifierFlags);
    }

    public static RegexOptions BuildOptions(ParsedCommandArguments parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        if (parsed.HasFlag("i", "ignore-case"))
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (parsed.HasFlag("m", "multiline"))
        {
            options |= RegexOptions.Multiline;
        }

        if (parsed.HasFlag("s", "singleline", "dotall"))
        {
            options |= RegexOptions.Singleline;
        }

        if (parsed.HasFlag("x", "ignore-pattern-whitespace"))
        {
            options |= RegexOptions.IgnorePatternWhitespace;
        }

        if (parsed.HasFlag("explicit-capture"))
        {
            options |= RegexOptions.ExplicitCapture;
        }

        return options;
    }

    public static Regex RequireRegex(
        CommandContext context,
        ParsedCommandArguments parsed,
        object? value,
        string label,
        int argumentIndex = 0,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parsed);

        if (value is Regex regex)
        {
            if (HasModifierFlags(parsed))
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.regex_flags_not_applicable",
                    title: "Regex option flags only apply to string patterns.",
                    argumentIndex: argumentIndex,
                    label: "this is already a compiled regex",
                    help: "use inline modifiers like `(?im)` or construct the regex with the options you want.");
            }

            return regex;
        }

        if (value is not string pattern)
        {
            throw new InvalidOperationException($"Argument '{label}' must be a string or regex value.");
        }

        return CompileRegex(context, pattern, BuildOptions(parsed), argumentIndex, timeout);
    }

    public static Regex CompileRegex(
        CommandContext context,
        string pattern,
        RegexOptions options,
        int argumentIndex = 0,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        try
        {
            return new Regex(pattern, options, timeout ?? TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException exception)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.invalid_regex",
                title: $"The regular expression is invalid. {exception.Message}",
                argumentIndex: argumentIndex,
                label: "this regex could not be compiled");
        }
    }

    public static string RequirePatternText(object? value, string label)
    {
        return value switch
        {
            string text => text,
            Regex regex => regex.ToString(),
            _ => throw new InvalidOperationException($"Argument '{label}' must be a string or regex value."),
        };
    }
}

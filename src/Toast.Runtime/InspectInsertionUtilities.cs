using System.Globalization;
using System.Numerics;

namespace Tosh.Runtime;

internal static class InspectInsertionUtilities
{
    public static string? TryBuildRootExpression(object? value)
    {
        return value switch
        {
            null => "null",
            string text => "\"" + EscapeString(text) + "\"",
            char character => "\"" + EscapeString(character.ToString()) + "\"",
            bool boolean => boolean ? "true" : "false",
            sbyte number => number.ToString(CultureInfo.InvariantCulture),
            byte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            ushort number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            ulong number => number.ToString(CultureInfo.InvariantCulture),
            Half number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            BigInteger number => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    public static string? BuildInsertionSegment(InspectTreeNodeKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return kind switch
        {
            InspectTreeNodeKind.Property or InspectTreeNodeKind.Field => BuildMemberSegment(text),
            InspectTreeNodeKind.Item => text,
            InspectTreeNodeKind.Method => BuildMethodSegment(text),
            InspectTreeNodeKind.Interface => null,
            _ => null,
        };
    }

    public static string ComposeInsertionText(string? rootExpression, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrWhiteSpace(rootExpression))
        {
            return path.StartsWith(".", StringComparison.Ordinal) ? path[1..] : path;
        }

        var trimmedRoot = rootExpression.Trim();

        if (path.Length > 0 &&
            (path.StartsWith(".", StringComparison.Ordinal) || path.StartsWith("[", StringComparison.Ordinal)) &&
            ShouldParenthesizeRootExpression(trimmedRoot))
        {
            trimmedRoot = "(" + trimmedRoot + ")";
        }

        return trimmedRoot + path;
    }

    private static string BuildMemberSegment(string name)
    {
        return IsSimpleIdentifier(name)
            ? "." + name
            : $"[\"{EscapeString(name)}\",]";
    }

    private static string? BuildMethodSegment(string signature)
    {
        var parenIndex = signature.IndexOf('(');
        var methodName = parenIndex >= 0 ? signature[..parenIndex] : signature;
        methodName = methodName.Trim();

        var lastSpaceIndex = methodName.LastIndexOf(' ');
        if (lastSpaceIndex >= 0 && lastSpaceIndex + 1 < methodName.Length)
        {
            methodName = methodName[(lastSpaceIndex + 1)..];
        }

        if (string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        return "." + methodName + "()";
    }

    private static bool IsSimpleIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index += 1)
        {
            if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string EscapeString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static bool ShouldParenthesizeRootExpression(string rootExpression)
    {
        if (rootExpression.Length == 0 ||
            (rootExpression[0] == '(' && rootExpression[^1] == ')') ||
            rootExpression[0] == '$')
        {
            return false;
        }

        return !IsSimpleMemberAccessRoot(rootExpression);
    }

    private static bool IsSimpleMemberAccessRoot(string value)
    {
        var expectsIdentifierStart = true;

        for (var index = 0; index < value.Length; index += 1)
        {
            var character = value[index];

            if (character == '.')
            {
                expectsIdentifierStart = true;
                continue;
            }

            if (expectsIdentifierStart)
            {
                if (!(char.IsLetter(character) || character == '_'))
                {
                    return false;
                }

                expectsIdentifierStart = false;
                continue;
            }

            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return !expectsIdentifierStart;
    }
}

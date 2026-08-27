using System.Globalization;
using System.Text.RegularExpressions;

namespace Tosh.Runtime;

internal static partial class UnixFileModeParser
{
    public static UnixFileMode Parse(string text, UnixFileMode currentMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var trimmed = text.Trim();

        if (OctalModeRegex().IsMatch(trimmed))
        {
            return ParseOctal(trimmed);
        }

        var mode = currentMode;

        foreach (var clause in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mode = ApplyClause(mode, clause);
        }

        return mode;
    }

    private static UnixFileMode ParseOctal(string text)
    {
        var digits = text.Length == 4 ? text[1..] : text;
        var special = text.Length == 4 ? text[0] - '0' : 0;
        var value = 0;

        foreach (var digit in digits)
        {
            value = (value * 8) + (digit - '0');
        }

        var mode = (UnixFileMode)value;

        if ((special & 4) != 0)
        {
            mode |= UnixFileMode.SetUser;
        }

        if ((special & 2) != 0)
        {
            mode |= UnixFileMode.SetGroup;
        }

        if ((special & 1) != 0)
        {
            mode |= UnixFileMode.StickyBit;
        }

        return mode;
    }

    private static UnixFileMode ApplyClause(UnixFileMode mode, string clause)
    {
        var index = clause.IndexOfAny(['+', '-', '=']);

        if (index < 0 || index == clause.Length - 1)
        {
            throw new InvalidOperationException($"Unsupported symbolic mode clause '{clause}'.");
        }

        var whoPart = clause[..index];
        var operation = clause[index];
        var permissionsPart = clause[(index + 1)..];
        var targets = ParseTargets(whoPart);
        var permissionBits = ParsePermissionBits(targets, permissionsPart);

        return operation switch
        {
            '+' => mode | permissionBits,
            '-' => mode & ~permissionBits,
            '=' => (mode & ~GetClearMask(targets)) | permissionBits,
            _ => throw new InvalidOperationException($"Unsupported symbolic mode operator '{operation}'."),
        };
    }

    private static PermissionTargets ParseTargets(string text)
    {
        var targets = PermissionTargets.None;

        foreach (var character in text)
        {
            targets |= character switch
            {
                'u' => PermissionTargets.User,
                'g' => PermissionTargets.Group,
                'o' => PermissionTargets.Other,
                'a' => PermissionTargets.All,
                _ => throw new InvalidOperationException($"Unsupported symbolic mode target '{character}'."),
            };
        }

        return targets == PermissionTargets.None ? PermissionTargets.All : targets;
    }

    private static UnixFileMode ParsePermissionBits(PermissionTargets targets, string permissionsPart)
    {
        var mode = UnixFileMode.None;

        foreach (var character in permissionsPart)
        {
            switch (character)
            {
                case 'r':
                    if (targets.HasFlag(PermissionTargets.User)) mode |= UnixFileMode.UserRead;
                    if (targets.HasFlag(PermissionTargets.Group)) mode |= UnixFileMode.GroupRead;
                    if (targets.HasFlag(PermissionTargets.Other)) mode |= UnixFileMode.OtherRead;
                    break;
                case 'w':
                    if (targets.HasFlag(PermissionTargets.User)) mode |= UnixFileMode.UserWrite;
                    if (targets.HasFlag(PermissionTargets.Group)) mode |= UnixFileMode.GroupWrite;
                    if (targets.HasFlag(PermissionTargets.Other)) mode |= UnixFileMode.OtherWrite;
                    break;
                case 'x':
                    if (targets.HasFlag(PermissionTargets.User)) mode |= UnixFileMode.UserExecute;
                    if (targets.HasFlag(PermissionTargets.Group)) mode |= UnixFileMode.GroupExecute;
                    if (targets.HasFlag(PermissionTargets.Other)) mode |= UnixFileMode.OtherExecute;
                    break;
                case 's':
                    if (targets.HasFlag(PermissionTargets.User)) mode |= UnixFileMode.SetUser;
                    if (targets.HasFlag(PermissionTargets.Group)) mode |= UnixFileMode.SetGroup;
                    break;
                case 't':
                    mode |= UnixFileMode.StickyBit;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported symbolic mode permission '{character}'.");
            }
        }

        return mode;
    }

    private static UnixFileMode GetClearMask(PermissionTargets targets)
    {
        var mask = UnixFileMode.None;

        if (targets.HasFlag(PermissionTargets.User))
        {
            mask |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.SetUser;
        }

        if (targets.HasFlag(PermissionTargets.Group))
        {
            mask |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
        }

        if (targets.HasFlag(PermissionTargets.Other))
        {
            mask |= UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit;
        }

        return mask;
    }

    [GeneratedRegex("^[0-7]{3,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex OctalModeRegex();

    [Flags]
    private enum PermissionTargets
    {
        None = 0,
        User = 1,
        Group = 2,
        Other = 4,
        All = User | Group | Other,
    }
}

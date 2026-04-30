using System.Globalization;
using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandArgument("mask", "The octal file creation mask to set (e.g. 022, 077).", Required = false)]
[CommandExample("umask", Title = "Display the current umask")]
[CommandExample("umask 022", Title = "Set umask to 022 (owner: all, group/other: no write)")]
[CommandExample("umask 077", Title = "Set umask to 077 (owner: all, group/other: no access)")]
[CommandNote("Controls the default permission bits removed from newly created files and directories. Without arguments, displays the current mask in octal. Only available on Unix-like systems.")]
[CommandOutput("The current umask as a zero-padded octal string (e.g. '0022'), or nothing when setting.")]
public sealed class UmaskCommand : ShellCommand
{
    public UmaskCommand()
        : base("umask", "Display or set the file creation mask.", "umask [mask]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.umask_unsupported",
                title: "`umask` is not supported on Windows.",
                label: "this command requires a Unix-like operating system");
        }

        if (context.Arguments.Count == 0)
        {
            // Get current mask by setting to 0 and restoring.
            var current = NativeUmask.umask(0);
            NativeUmask.umask(current);
            yield return current.ToString("D4", CultureInfo.InvariantCulture).Insert(0, "0")[..4];
            yield break;
        }

        var arg = context.Arguments[0]?.ToString() ?? string.Empty;

        if (!TryParseOctal(arg, out var mask))
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.umask_invalid",
                title: $"Invalid umask value '{arg}'.",
                label: "expected an octal number (e.g. 022, 077)",
                help: "The mask must be an octal value between 000 and 777.");
        }

        NativeUmask.umask(mask);
        yield break;
    }

    private static bool TryParseOctal(string value, out int result)
    {
        result = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is < '0' or > '7')
            {
                return false;
            }

            result = (result * 8) + (ch - '0');
        }

        return result is >= 0 and <= 0x1FF; // 0-777 octal
    }

    private static class NativeUmask
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "umask")]
        public static extern int umask(int mask);
    }
}

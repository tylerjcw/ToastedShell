using System.Diagnostics;
using System.Globalization;

namespace Tosh.Core;

internal static class ProcessMetadataUtilities
{
    public static ProcessSupplementalInfo Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsLinux())
        {
            return new ProcessSupplementalInfo(null, null, null);
        }

        return ReadLinux(process.Id);
    }

    private static ProcessSupplementalInfo ReadLinux(int processId)
    {
        var statusPath = $"/proc/{processId}/status";
        int? parentId = null;
        FileSystemPrincipalInfo? user = null;

        try
        {
            if (File.Exists(statusPath))
            {
                foreach (var line in File.ReadLines(statusPath))
                {
                    if (line.StartsWith("PPid:", StringComparison.Ordinal))
                    {
                        if (TryReadFirstIntegerField(line, out var parsedParentId))
                        {
                            parentId = parsedParentId;
                        }

                        continue;
                    }

                    if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    {
                        if (TryReadFirstUnsignedField(line, out var uid))
                        {
                            user = UnixSystemServices.TryGetUser(uid) ?? new FileSystemPrincipalInfo(uid, uid.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return new ProcessSupplementalInfo(parentId, user, TryReadTerminal(processId));
    }

    private static string? TryReadTerminal(int processId)
    {
        try
        {
            var info = new FileInfo($"/proc/{processId}/fd/0");
            var target = info.LinkTarget;

            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            if (target.StartsWith("/dev/", StringComparison.Ordinal))
            {
                return target["/dev/".Length..];
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadFirstIntegerField(string line, out int value)
    {
        value = 0;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadFirstUnsignedField(string line, out uint value)
    {
        value = 0;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

internal sealed record ProcessSupplementalInfo(
    int? ParentId,
    FileSystemPrincipalInfo? User,
    string? Tty);

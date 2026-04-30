using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Tosh.Runtime;

internal static class ProcessMetadataUtilities
{
    public static ProcessSupplementalInfo Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (OperatingSystem.IsLinux())
        {
            return ReadLinux(process.Id);
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindows(process.Id);
        }

        return new ProcessSupplementalInfo(null, null, null);
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

    [SupportedOSPlatform("windows")]
    private static ProcessSupplementalInfo ReadWindows(int processId)
    {
        IntPtr processHandle = IntPtr.Zero;

        try
        {
            processHandle = Interop.OpenProcess(Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

            if (processHandle == IntPtr.Zero)
            {
                return new ProcessSupplementalInfo(null, null, null);
            }

            return new ProcessSupplementalInfo(
                TryReadWindowsParentId(processHandle),
                TryReadWindowsUser(processHandle),
                null);
        }
        catch
        {
            return new ProcessSupplementalInfo(null, null, null);
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                Interop.CloseHandle(processHandle);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? TryReadWindowsParentId(IntPtr processHandle)
    {
        try
        {
            var result = Interop.NtQueryInformationProcess(
                processHandle,
                processInformationClass: 0,
                out var info,
                Marshal.SizeOf<Interop.ProcessBasicInformation>(),
                out _);

            if (result != 0)
            {
                return null;
            }

            var parentId = info.InheritedFromUniqueProcessId.ToInt64();

            return parentId is > 0 and <= int.MaxValue
                ? (int)parentId
                : null;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemPrincipalInfo? TryReadWindowsUser(IntPtr processHandle)
    {
        IntPtr tokenHandle = IntPtr.Zero;
        IntPtr tokenBuffer = IntPtr.Zero;

        try
        {
            if (!Interop.OpenProcessToken(processHandle, Interop.TOKEN_QUERY, out tokenHandle) || tokenHandle == IntPtr.Zero)
            {
                return null;
            }

            Interop.GetTokenInformation(
                tokenHandle,
                Interop.TokenInformationClass.TokenUser,
                IntPtr.Zero,
                0,
                out var requiredSize);

            if (requiredSize <= 0)
            {
                return null;
            }

            tokenBuffer = Marshal.AllocHGlobal(requiredSize);

            if (!Interop.GetTokenInformation(
                tokenHandle,
                Interop.TokenInformationClass.TokenUser,
                tokenBuffer,
                requiredSize,
                out _))
            {
                return null;
            }

            var tokenUser = Marshal.PtrToStructure<Interop.TokenUser>(tokenBuffer);

            if (tokenUser.User.Sid == IntPtr.Zero)
            {
                return null;
            }

            var sid = new SecurityIdentifier(tokenUser.User.Sid);
            return UnixSystemServices.CreateWindowsPrincipalInfo(sid, UnixSystemServices.TranslateSid(sid));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (tokenBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(tokenBuffer);
            }

            if (tokenHandle != IntPtr.Zero)
            {
                Interop.CloseHandle(tokenHandle);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Interop
    {
        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        internal const uint TOKEN_QUERY = 0x0008;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            TokenInformationClass tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            out ProcessBasicInformation processInformation,
            int processInformationLength,
            out int returnLength);

        internal enum TokenInformationClass
        {
            TokenUser = 1,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessBasicInformation
        {
            internal IntPtr Reserved1;
            internal IntPtr PebBaseAddress;
            internal IntPtr Reserved2_0;
            internal IntPtr Reserved2_1;
            internal IntPtr UniqueProcessId;
            internal IntPtr InheritedFromUniqueProcessId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SidAndAttributes
        {
            internal IntPtr Sid;
            internal uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TokenUser
        {
            internal SidAndAttributes User;
        }
    }
}

internal sealed record ProcessSupplementalInfo(
    int? ParentId,
    FileSystemPrincipalInfo? User,
    string? Tty);

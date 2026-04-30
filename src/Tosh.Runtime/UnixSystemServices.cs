using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Tosh.Runtime;

internal static class UnixSystemServices
{
    private static readonly Regex MountEscapePattern = new(@"\\([0-7]{3})", RegexOptions.Compiled);

    public static UnameInfo GetUname()
    {
        if (TryGetUnameFromLibc(out var info))
        {
            return info;
        }

        return UnameInfo.Fallback();
    }

    public static string GetHostName() => GetUname().NodeName;

    public static UserIdentityInfo GetCurrentIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsIdentity();
        }

        if (!IsUnixLike())
        {
            var fallbackUser = new FileSystemPrincipalInfo(0, Environment.UserName);
            var fallbackGroup = new FileSystemPrincipalInfo(0, null);
            return new UserIdentityInfo(fallbackUser, 0, 0, fallbackGroup, 0, 0, [fallbackGroup]);
        }

        var uid = Interop.getuid();
        var euid = Interop.geteuid();
        var gid = Interop.getgid();
        var egid = Interop.getegid();

        var user = TryGetPasswd(uid) ?? new FileSystemPrincipalInfo(uid, Environment.UserName);
        var group = TryGetGroup(gid) ?? new FileSystemPrincipalInfo(gid, null);
        var groups = GetSupplementaryGroups(gid);

        return new UserIdentityInfo(user, uid, euid, group, gid, egid, groups);
    }

    public static FileSystemPrincipalInfo? TryGetUser(uint uid)
    {
        return TryGetPasswd(uid);
    }

    public static IReadOnlyList<FileSystemUsageInfo> GetFileSystemUsage()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo"))
        {
            var entries = ReadLinuxMountInfo();

            if (entries.Count > 0)
            {
                return entries;
            }
        }

        return DriveInfo.GetDrives()
            .Select(drive => CreateUsageInfo(
                fileSystem: drive.Name,
                mountedOn: drive.Name,
                type: SafeGet(() => drive.DriveFormat, (string?)null),
                driveType: SafeGet(() => drive.DriveType, (DriveType?)null),
                mountRoot: "/",
                size: TryGetStorageSize(() => drive.TotalSize),
                used: TryGetUsedStorageSize(drive),
                available: TryGetStorageSize(() => drive.AvailableFreeSpace)))
            .OrderBy(entry => entry.MountedOn, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsUnixLike() =>
        OperatingSystem.IsLinux() ||
        OperatingSystem.IsMacOS() ||
        OperatingSystem.IsFreeBSD();

    [SupportedOSPlatform("windows")]
    private static UserIdentityInfo GetWindowsIdentity()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = CreateWindowsPrincipalInfo(identity.User, identity.Name);
            var groups = GetWindowsGroups(identity);
            var primaryGroup = groups.FirstOrDefault() ?? new FileSystemPrincipalInfo(0, "None");
            var uid = ToPrincipalId(user.Id);
            var gid = ToPrincipalId(primaryGroup.Id);

            return new UserIdentityInfo(user, uid, uid, primaryGroup, gid, gid, groups);
        }
        catch
        {
            var fallbackUser = new FileSystemPrincipalInfo(0, Environment.UserName);
            var fallbackGroup = new FileSystemPrincipalInfo(0, "Users");
            return new UserIdentityInfo(fallbackUser, 0, 0, fallbackGroup, 0, 0, [fallbackGroup]);
        }
    }

    private static bool TryGetUnameFromLibc(out UnameInfo info)
    {
        try
        {
            if (Interop.uname(out var native) == 0)
            {
                info = new UnameInfo(
                    native.sysname ?? string.Empty,
                    native.nodename ?? string.Empty,
                    native.release ?? string.Empty,
                    native.version ?? string.Empty,
                    native.machine ?? string.Empty,
                    native.sysname ?? string.Empty);
                return true;
            }
        }
        catch
        {
        }

        info = null!;
        return false;
    }

    private static IReadOnlyList<FileSystemPrincipalInfo> GetSupplementaryGroups(uint primaryGroupId)
    {
        try
        {
            var count = Interop.getgroups(0, null);

            if (count < 0)
            {
                return [TryGetGroup(primaryGroupId) ?? new FileSystemPrincipalInfo(primaryGroupId, null)];
            }

            var groupIds = new uint[count];

            if (count > 0 && Interop.getgroups(count, groupIds) < 0)
            {
                return [TryGetGroup(primaryGroupId) ?? new FileSystemPrincipalInfo(primaryGroupId, null)];
            }

            return groupIds
                .Where(groupId => groupId != primaryGroupId)
                .Prepend(primaryGroupId)
                .Distinct()
                .Select(groupId => TryGetGroup(groupId) ?? new FileSystemPrincipalInfo(groupId, null))
                .ToArray();
        }
        catch
        {
            return [TryGetGroup(primaryGroupId) ?? new FileSystemPrincipalInfo(primaryGroupId, null)];
        }
    }

    private static FileSystemPrincipalInfo? TryGetPasswd(uint uid)
    {
        try
        {
            var pointer = Interop.getpwuid(uid);

            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            var native = Marshal.PtrToStructure<Interop.Passwd>(pointer);
            return new FileSystemPrincipalInfo(uid, PtrToAnsiString(native.pw_name));
        }
        catch
        {
            return null;
        }
    }

    private static FileSystemPrincipalInfo? TryGetGroup(uint gid)
    {
        try
        {
            var pointer = Interop.getgrgid(gid);

            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            var native = Marshal.PtrToStructure<Interop.Group>(pointer);
            return new FileSystemPrincipalInfo(gid, PtrToAnsiString(native.gr_name));
        }
        catch
        {
            return null;
        }
    }

    private static string? PtrToAnsiString(IntPtr pointer) =>
        pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<FileSystemPrincipalInfo> GetWindowsGroups(WindowsIdentity identity)
    {
        try
        {
            if (identity.Groups is null || identity.Groups.Count == 0)
            {
                return [new FileSystemPrincipalInfo(0, "Users")];
            }

            var groups = new List<FileSystemPrincipalInfo>();
            var seen = new HashSet<long>();

            foreach (var reference in identity.Groups)
            {
                SecurityIdentifier? sid = null;

                try
                {
                    sid = reference as SecurityIdentifier ??
                          reference.Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
                }
                catch
                {
                }

                var info = CreateWindowsPrincipalInfo(sid, TranslateSid(sid) ?? reference.Value);

                if (seen.Add(info.Id))
                {
                    groups.Add(info);
                }
            }

            return groups.Count > 0
                ? groups
                : [new FileSystemPrincipalInfo(0, "Users")];
        }
        catch
        {
            return [new FileSystemPrincipalInfo(0, "Users")];
        }
    }

    [SupportedOSPlatform("windows")]
    internal static FileSystemPrincipalInfo CreateWindowsPrincipalInfo(SecurityIdentifier? sid, string? fallbackName)
    {
        var name = TranslateSid(sid) ?? fallbackName;
        var id = sid is null
            ? 0
            : GetWindowsPrincipalId(sid.Value);
        return new FileSystemPrincipalInfo(id, name);
    }

    [SupportedOSPlatform("windows")]
    internal static string? TranslateSid(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return null;
        }

        try
        {
            return (sid.Translate(typeof(NTAccount)) as NTAccount)?.Value ?? sid.Value;
        }
        catch
        {
            return sid.Value;
        }
    }

    [SupportedOSPlatform("windows")]
    private static long GetWindowsPrincipalId(string sid)
    {
        var separator = sid.LastIndexOf('-');

        if (separator >= 0 &&
            long.TryParse(sid[(separator + 1)..], out var rid))
        {
            return rid;
        }

        unchecked
        {
            const long offset = 1469598103934665603;
            const long prime = 1099511628211;
            long hash = offset;

            foreach (var character in sid)
            {
                hash ^= character;
                hash *= prime;
            }

            return hash < 0 ? -hash : hash;
        }
    }

    private static uint ToPrincipalId(long value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= uint.MaxValue
            ? uint.MaxValue
            : (uint)value;
    }

    private static IReadOnlyList<FileSystemUsageInfo> ReadLinuxMountInfo()
    {
        var entries = new List<FileSystemUsageInfo>();

        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            if (!TryParseMountInfo(line, out var mountInfo))
            {
                continue;
            }

            var drive = TryCreateDriveInfo(mountInfo.MountedOn);
            var size = drive is null ? null : TryGetStorageSize(() => drive.TotalSize);
            var used = drive is null ? null : TryGetUsedStorageSize(drive);
            var available = drive is null ? null : TryGetStorageSize(() => drive.AvailableFreeSpace);
            var driveType = drive is null ? null : SafeGet(() => drive.DriveType, (DriveType?)null);

            entries.Add(CreateUsageInfo(
                fileSystem: mountInfo.FileSystem,
                mountedOn: mountInfo.MountedOn,
                type: mountInfo.Type,
                driveType: driveType,
                mountRoot: mountInfo.Root,
                size: size,
                used: used,
                available: available,
                inodeInfo: TryGetInodeInfo(mountInfo.MountedOn)));
        }

        return entries
            .DistinctBy(entry => entry.MountedOn, StringComparer.Ordinal)
            .OrderBy(entry => entry.MountedOn, StringComparer.Ordinal)
            .ToArray();
    }

    private static (long Total, long Free, long Used)? TryGetInodeInfo(string mountPoint)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var buf = new Statvfs();

            if (statvfs(mountPoint, ref buf) != 0)
            {
                return null;
            }

            var total = (long)buf.f_files;
            var free = (long)buf.f_ffree;

            if (total <= 0)
            {
                return null;
            }

            return (total, free, total - free);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statvfs([MarshalAs(UnmanagedType.LPStr)] string path, ref Statvfs buf);

    [StructLayout(LayoutKind.Sequential)]
    private struct Statvfs
    {
        public ulong f_bsize;
        public ulong f_frsize;
        public ulong f_blocks;
        public ulong f_bfree;
        public ulong f_bavail;
        public ulong f_files;
        public ulong f_ffree;
        public ulong f_favail;
        public ulong f_fsid;
        public ulong f_flag;
        public ulong f_namemax;
    }

    private static FileSystemUsageInfo CreateUsageInfo(
        string fileSystem,
        string mountedOn,
        string? type,
        DriveType? driveType,
        string? mountRoot,
        StorageSize? size,
        StorageSize? used,
        StorageSize? available,
        (long Total, long Free, long Used)? inodeInfo = null)
    {
        int? usePercent = null;

        if (used is { } usedSize && available is { } availableSize)
        {
            var denominator = usedSize.Bytes + availableSize.Bytes;

            if (denominator > 0)
            {
                usePercent = (int)Math.Round(100d * usedSize.Bytes / denominator, MidpointRounding.AwayFromZero);
            }
        }

        long? inodesTotal = null;
        long? inodesUsed = null;
        long? inodesFree = null;
        int? inodeUsePercent = null;

        if (inodeInfo is { } inode)
        {
            inodesTotal = inode.Total;
            inodesUsed = inode.Used;
            inodesFree = inode.Free;

            if (inode.Total > 0)
            {
                inodeUsePercent = (int)Math.Round(100d * inode.Used / inode.Total, MidpointRounding.AwayFromZero);
            }
        }

        return new FileSystemUsageInfo(
            fileSystem,
            mountedOn,
            type,
            size,
            used,
            available,
            usePercent,
            driveType,
            IsLocal: DetermineIsLocal(type, driveType),
            MountRoot: mountRoot,
            InodesTotal: inodesTotal,
            InodesUsed: inodesUsed,
            InodesFree: inodesFree,
            InodeUsePercent: inodeUsePercent);
    }

    private static bool DetermineIsLocal(string? type, DriveType? driveType)
    {
        if (driveType == DriveType.Network)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return true;
        }

        return !RemoteFileSystemTypes.Contains(type);
    }

    private static DriveInfo? TryCreateDriveInfo(string mountedOn)
    {
        try
        {
            return new DriveInfo(mountedOn);
        }
        catch
        {
            return null;
        }
    }

    private static readonly HashSet<string> RemoteFileSystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nfs",
        "nfs4",
        "cifs",
        "smbfs",
        "smb3",
        "sshfs",
        "fuse.sshfs",
        "9p",
        "ceph",
        "afs",
        "gfs",
        "glusterfs",
        "davfs",
        "ftpfs",
        "curlftpfs",
    };

    private static bool TryParseMountInfo(string line, out LinuxMountInfo mountInfo)
    {
        mountInfo = default;

        var separatorIndex = line.IndexOf(" - ", StringComparison.Ordinal);

        if (separatorIndex < 0)
        {
            return false;
        }

        var left = line[..separatorIndex];
        var right = line[(separatorIndex + 3)..];
        var leftParts = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (leftParts.Length < 5 || rightParts.Length < 2)
        {
            return false;
        }

        mountInfo = new LinuxMountInfo(
            FileSystem: UnescapeMountField(rightParts[1]),
            Root: UnescapeMountField(leftParts[3]),
            MountedOn: UnescapeMountField(leftParts[4]),
            Type: rightParts[0]);
        return true;
    }

    private static string UnescapeMountField(string value)
    {
        return MountEscapePattern.Replace(
            value,
            match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());
    }

    private static StorageSize? TryGetStorageSize(Func<long> getBytes)
    {
        try
        {
            return StorageSize.FromBytes(getBytes());
        }
        catch
        {
            return null;
        }
    }

    private static StorageSize? TryGetUsedStorageSize(DriveInfo drive)
    {
        try
        {
            var total = drive.TotalSize;
            var free = drive.TotalFreeSpace;
            return StorageSize.FromBytes(Math.Max(0, total - free));
        }
        catch
        {
            return null;
        }
    }

    private static T? SafeGet<T>(Func<T> getValue, T? fallback)
    {
        try
        {
            return getValue();
        }
        catch
        {
            return fallback;
        }
    }

    private readonly record struct LinuxMountInfo(string FileSystem, string Root, string MountedOn, string Type);

    private static class Interop
    {
        [DllImport("libc", SetLastError = true)]
        public static extern int uname(out UtsName buffer);

        [DllImport("libc", SetLastError = true)]
        public static extern uint getuid();

        [DllImport("libc", SetLastError = true)]
        public static extern uint geteuid();

        [DllImport("libc", SetLastError = true)]
        public static extern uint getgid();

        [DllImport("libc", SetLastError = true)]
        public static extern uint getegid();

        [DllImport("libc", SetLastError = true)]
        public static extern int getgroups(int size, [Out] uint[]? list);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr getpwuid(uint uid);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr getgrgid(uint gid);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct UtsName
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
            public string sysname;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
            public string nodename;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
            public string release;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
            public string version;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
            public string machine;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 65)]
            public string domainname;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Passwd
        {
            public IntPtr pw_name;
            public IntPtr pw_passwd;
            public uint pw_uid;
            public uint pw_gid;
            public IntPtr pw_gecos;
            public IntPtr pw_dir;
            public IntPtr pw_shell;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Group
        {
            public IntPtr gr_name;
            public IntPtr gr_passwd;
            public uint gr_gid;
            public IntPtr gr_mem;
        }
    }
}

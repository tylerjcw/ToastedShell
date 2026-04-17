using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace Tosh.Core;

internal sealed record UnixFileSystemMetadata(
    FileSystemPrincipalInfo? Owner,
    FileSystemPrincipalInfo? Group,
    long? Inode,
    long? LinkCount)
{
    // Cache uid→name and gid→name lookups. Most directory listings share the same owner/group,
    // so this avoids thousands of redundant getpwuid()/getgrgid() P/Invoke calls.
    private static readonly Dictionary<uint, FileSystemPrincipalInfo> UserCache = new();
    private static readonly Dictionary<uint, FileSystemPrincipalInfo> GroupCache = new();

    public static UnixFileSystemMetadata? TryRead(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (OperatingSystem.IsWindows())
        {
            return TryReadWindows(entry);
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            if (LStat(entry.FullName, out var stat) != 0)
            {
                return null;
            }

            return new UnixFileSystemMetadata(
                ResolveUser(stat.st_uid),
                ResolveGroup(stat.st_gid),
                checked((long)stat.st_ino),
                checked((long)stat.st_nlink));
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static UnixFileSystemMetadata? TryReadWindows(FileSystemInfo entry)
    {
        try
        {
            FileSystemSecurity security = entry switch
            {
                DirectoryInfo directory => directory.GetAccessControl(),
                FileInfo file => file.GetAccessControl(),
                _ => new FileInfo(entry.FullName).GetAccessControl(),
            };

            var ownerSid = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var groupSid = security.GetGroup(typeof(SecurityIdentifier)) as SecurityIdentifier;

            return new UnixFileSystemMetadata(
                ownerSid is null ? null : UnixSystemServices.CreateWindowsPrincipalInfo(ownerSid, UnixSystemServices.TranslateSid(ownerSid)),
                groupSid is null ? null : UnixSystemServices.CreateWindowsPrincipalInfo(groupSid, UnixSystemServices.TranslateSid(groupSid)),
                null,
                null);
        }
        catch
        {
            return null;
        }
    }

    private static FileSystemPrincipalInfo ResolveUser(uint uid)
    {
        if (UserCache.TryGetValue(uid, out var cached))
        {
            return cached;
        }

        var pointer = GetPwUid(uid);

        FileSystemPrincipalInfo result;
        if (pointer == IntPtr.Zero)
        {
            result = new FileSystemPrincipalInfo(uid, null);
        }
        else
        {
            var passwd = Marshal.PtrToStructure<Passwd>(pointer);
            result = new FileSystemPrincipalInfo(uid, Marshal.PtrToStringAnsi(passwd.pw_name));
        }

        UserCache[uid] = result;
        return result;
    }

    private static FileSystemPrincipalInfo ResolveGroup(uint gid)
    {
        if (GroupCache.TryGetValue(gid, out var cached))
        {
            return cached;
        }

        var pointer = GetGrGid(gid);

        FileSystemPrincipalInfo result;
        if (pointer == IntPtr.Zero)
        {
            result = new FileSystemPrincipalInfo(gid, null);
        }
        else
        {
            var group = Marshal.PtrToStructure<PosixGroup>(pointer);
            result = new FileSystemPrincipalInfo(gid, Marshal.PtrToStringAnsi(group.gr_name));
        }

        GroupCache[gid] = result;
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Stat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public Timespec st_atim;
        public Timespec st_mtim;
        public Timespec st_ctim;
        public long __glibc_reserved0;
        public long __glibc_reserved1;
        public long __glibc_reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
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
    private struct PosixGroup
    {
        public IntPtr gr_name;
        public IntPtr gr_passwd;
        public uint gr_gid;
        public IntPtr gr_mem;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int LStat(string path, out Stat buffer);

    [DllImport("libc", SetLastError = true, EntryPoint = "getpwuid")]
    private static extern IntPtr GetPwUid(uint uid);

    [DllImport("libc", SetLastError = true, EntryPoint = "getgrgid")]
    private static extern IntPtr GetGrGid(uint gid);
}

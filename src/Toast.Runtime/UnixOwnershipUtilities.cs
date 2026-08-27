using System.Runtime.InteropServices;

namespace Tosh.Runtime;

internal static class UnixOwnershipUtilities
{
    public static uint? ResolveUserId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (uint.TryParse(text, out var numeric))
        {
            return numeric;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var pointer = Interop.getpwnam(text);

        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        var passwd = Marshal.PtrToStructure<Interop.Passwd>(pointer);
        return passwd.pw_uid;
    }

    public static uint? ResolveGroupId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (uint.TryParse(text, out var numeric))
        {
            return numeric;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var pointer = Interop.getgrnam(text);

        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        var group = Marshal.PtrToStructure<Interop.Group>(pointer);
        return group.gr_gid;
    }

    public static void ChangeOwnership(string path, uint? uid, uint? gid)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("chown is not supported on this platform.");
        }

        var result = Interop.chown(
            path,
            uid ?? uint.MaxValue,
            gid ?? uint.MaxValue);

        if (result != 0)
        {
            throw new InvalidOperationException($"Unable to change ownership for '{path}'.");
        }
    }

    private static class Interop
    {
        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr getpwnam(string name);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr getgrnam(string name);

        [DllImport("libc", SetLastError = true)]
        public static extern int chown(string path, uint owner, uint group);

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

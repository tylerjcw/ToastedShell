using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tosh.Core;

/// <summary>
/// Windows implementation of CPU information gathering.
/// Uses GetSystemInfo and GetLogicalProcessorInformationEx P/Invoke
/// combined with RuntimeInformation and optional Registry reads.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsCpuServices
{
    public static CpuInfo GetCpuInfo()
    {
        Interop.GetSystemInfo(out var si);

        var logicalCount = Environment.ProcessorCount;
        var arch = MapArchitecture(si.wProcessorArchitecture);
        var modelName = TryGetProcessorNameFromRegistry();
        var (coresPerSocket, socketCount) = TryGetCoreSocketCount(si.dwNumberOfProcessors);
        var threadsPerCore = coresPerSocket > 0 && socketCount > 0
            ? (int?)(logicalCount / (coresPerSocket * socketCount))
            : null;
        var opModes = BuildOpModes(si.wProcessorArchitecture);
        var byteOrder = BitConverter.IsLittleEndian ? "Little Endian" : "Big Endian";

        return new CpuInfo
        {
            Architecture = arch,
            CpuOpModes = opModes,
            ByteOrder = byteOrder,
            CpuCount = logicalCount,
            OnlineCpuList = logicalCount > 0 ? $"0-{logicalCount - 1}" : "0",
            ModelName = modelName,
            ThreadsPerCore = threadsPerCore,
            CoresPerSocket = coresPerSocket,
            SocketCount = socketCount,
        };
    }

    public static IReadOnlyList<CpuTopologyInfo> GetCpuTopology()
    {
        var count = Environment.ProcessorCount;
        var result = new List<CpuTopologyInfo>(count);
        var modelName = TryGetProcessorNameFromRegistry();

        for (var i = 0; i < count; i++)
        {
            result.Add(new CpuTopologyInfo
            {
                Cpu = i,
                Online = true,
                ModelName = modelName,
            });
        }

        return result;
    }

    private static string? TryGetProcessorNameFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            return key?.GetValue("ProcessorNameString") as string;
        }
        catch
        {
            return null;
        }
    }

    private static (int? coresPerSocket, int? socketCount) TryGetCoreSocketCount(uint logicalCount)
    {
        try
        {
            // Query SLPI buffer size first (call with null)
            Interop.GetLogicalProcessorInformationEx(
                RelationshipType.RelationProcessorCore,
                IntPtr.Zero,
                out var bufferSize);

            if (bufferSize == 0) return (null, null);

            var buffer = Marshal.AllocHGlobal((int)bufferSize);

            try
            {
                if (!Interop.GetLogicalProcessorInformationEx(
                    RelationshipType.RelationProcessorCore,
                    buffer,
                    out bufferSize))
                {
                    return (null, null);
                }

                var coreCount = 0;
                var offset = 0;

                while (offset < bufferSize)
                {
                    var relationship = Marshal.ReadInt32(buffer + offset);

                    if (relationship == (int)RelationshipType.RelationProcessorCore)
                    {
                        coreCount++;
                    }

                    // Size field is at offset 8 (DWORD)
                    var size = Marshal.ReadInt32(buffer + offset + 8);

                    if (size <= 0) break;

                    offset += size;
                }

                if (coreCount > 0)
                {
                    // Heuristic: assume 1 socket if we can't determine otherwise
                    return (coreCount, 1);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch { }

        return (null, null);
    }

    private static string MapArchitecture(ushort arch)
    {
        return arch switch
        {
            0 => "x86",
            5 => "arm",
            6 => "ia64",
            9 => "x86_64",
            12 => "aarch64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
    }

    private static IReadOnlyList<string> BuildOpModes(ushort arch)
    {
        return arch switch
        {
            9 => ["32-bit", "64-bit"],
            12 => ["32-bit", "64-bit"],
            0 => ["32-bit"],
            _ => [RuntimeInformation.OSArchitecture.ToString()],
        };
    }

    // ── P/Invoke ─────────────────────────────────────────────────────

    private enum RelationshipType
    {
        RelationProcessorCore = 0,
        RelationNumaNode = 1,
        RelationCache = 2,
        RelationProcessorPackage = 3,
        RelationGroup = 4,
        RelationAll = 0xffff,
    }

    private static class Interop
    {
        [DllImport("kernel32.dll")]
        internal static extern void GetSystemInfo(out SystemInfo lpSystemInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLogicalProcessorInformationEx(
            RelationshipType relationshipType,
            IntPtr buffer,
            out uint returnedLength);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemInfo
        {
            internal ushort wProcessorArchitecture;
            internal ushort wReserved;
            internal uint dwPageSize;
            internal IntPtr lpMinimumApplicationAddress;
            internal IntPtr lpMaximumApplicationAddress;
            internal IntPtr dwActiveProcessorMask;
            internal uint dwNumberOfProcessors;
            internal uint dwProcessorType;
            internal uint dwAllocationGranularity;
            internal ushort wProcessorLevel;
            internal ushort wProcessorRevision;
        }
    }
}

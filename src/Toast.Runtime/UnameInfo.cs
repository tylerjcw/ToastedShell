using System.Runtime.InteropServices;

namespace Tosh.Runtime;

public sealed record UnameInfo(
    string SystemName,
    string NodeName,
    string Release,
    string Version,
    string Machine,
    string OperatingSystem)
{
    public string SysName => SystemName;

    public string HostName => NodeName;

    public string Architecture => Machine;

    public static UnameInfo Fallback()
    {
        var osDescription = RuntimeInformation.OSDescription;
        var machine = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

        return new UnameInfo(
            System.OperatingSystem.IsWindows() ? "Windows" : Environment.OSVersion.Platform.ToString(),
            Environment.MachineName,
            Environment.OSVersion.Version.ToString(),
            osDescription,
            machine,
            osDescription);
    }
}

using System.Net;

namespace Tosh.Runtime;

public sealed record IpNeighborInfo(
    IPAddress? Address,
    string? Device,
    string? LinkLayerAddress,
    IReadOnlyList<string> State)
{
    public bool IsReachable => State.Contains("REACHABLE", StringComparer.OrdinalIgnoreCase);
    public bool IsStale => State.Contains("STALE", StringComparer.OrdinalIgnoreCase);
    public bool IsFailed => State.Contains("FAILED", StringComparer.OrdinalIgnoreCase);
    public string StateText => State.Count > 0 ? string.Join(", ", State) : string.Empty;
}

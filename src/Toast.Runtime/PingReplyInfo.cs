using System.Net;
using System.Net.NetworkInformation;

namespace Tosh.Runtime;

public sealed record PingReplyInfo(
    string Host,
    IPAddress? Address,
    int Sequence,
    IPStatus Status,
    TimeSpan? RoundtripTime,
    int Bytes,
    int? Ttl,
    bool? DontFragment)
{
    public double? TimeMs => RoundtripTime?.TotalMilliseconds;
}

namespace Tosh.Core;

public sealed record SystemUptimeInfo(
    TimeSpan Uptime,
    double Load1,
    double Load5,
    double Load15,
    DateTimeOffset SampledAt);

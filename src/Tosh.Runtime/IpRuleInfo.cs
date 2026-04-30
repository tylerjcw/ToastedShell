namespace Tosh.Runtime;

public sealed record IpRuleInfo(
    int? Priority,
    string? Source,
    string? Table,
    string? Action,
    string? Destination,
    string? IifName,
    string? OifName,
    int? FirewallMark,
    string? Protocol)
{
    public string SourceText => Source ?? "all";
    public string DestinationText => Destination ?? "all";
}

namespace Tosh.Core;

public sealed record SystemdNetworkLinkInfo(
    int Index,
    string Link,
    string Type,
    string OperationalState,
    string SetupState)
{
    public bool IsManaged => !string.Equals(SetupState, "unmanaged", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured => string.Equals(SetupState, "configured", StringComparison.OrdinalIgnoreCase);

    public bool IsRoutable => string.Equals(OperationalState, "routable", StringComparison.OrdinalIgnoreCase);

    public bool HasCarrier =>
        OperationalState is "dormant" or "carrier" or "degraded-carrier" or "degraded" or "enslaved" or "routable";

    public string DisplayState =>
        string.Equals(OperationalState, SetupState, StringComparison.OrdinalIgnoreCase)
            ? OperationalState
            : $"{OperationalState}/{SetupState}";

    public override string ToString()
    {
        return $"{Link} ({Type}, {DisplayState})";
    }
}

namespace Tosh.Runtime;

public sealed record SystemdUnitInfo(
    string Unit,
    string LoadState,
    string ActiveState,
    string SubState,
    string? Description)
{
    public string UnitType => SystemdParsingUtilities.GetUnitType(Unit);

    public bool IsLoaded => string.Equals(LoadState, "loaded", StringComparison.OrdinalIgnoreCase);

    public bool IsActive => string.Equals(ActiveState, "active", StringComparison.OrdinalIgnoreCase);

    public bool IsFailed =>
        string.Equals(ActiveState, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SubState, "failed", StringComparison.OrdinalIgnoreCase);

    public string DisplayState =>
        string.Equals(ActiveState, SubState, StringComparison.OrdinalIgnoreCase)
            ? ActiveState
            : $"{ActiveState}/{SubState}";

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Description)
            ? $"{Unit} ({DisplayState})"
            : $"{Unit} ({DisplayState}) - {Description}";
    }
}

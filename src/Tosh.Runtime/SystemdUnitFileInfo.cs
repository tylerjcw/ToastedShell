namespace Tosh.Runtime;

public sealed record SystemdUnitFileInfo(
    string UnitFile,
    string State,
    string? Preset)
{
    public string UnitType => SystemdParsingUtilities.GetUnitType(UnitFile);

    public bool IsEnabled =>
        State.StartsWith("enabled", StringComparison.OrdinalIgnoreCase) ||
        State.StartsWith("linked", StringComparison.OrdinalIgnoreCase);

    public bool IsMasked => State.StartsWith("masked", StringComparison.OrdinalIgnoreCase);

    public bool IsStatic => string.Equals(State, "static", StringComparison.OrdinalIgnoreCase);

    public string DisplayState =>
        string.IsNullOrWhiteSpace(Preset)
            ? State
            : $"{State} (preset: {Preset})";

    public override string ToString()
    {
        return $"{UnitFile} ({DisplayState})";
    }
}

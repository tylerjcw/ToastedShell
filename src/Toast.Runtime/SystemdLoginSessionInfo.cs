namespace Tosh.Runtime;

public sealed record SystemdLoginSessionInfo(
    string Session,
    int UserId,
    string User,
    string? Seat,
    int? Leader,
    string? Class,
    string? Tty,
    bool Idle,
    DateTimeOffset? Since)
{
    public override string ToString()
    {
        var state = Idle ? "idle" : "active";
        return string.IsNullOrWhiteSpace(Seat)
            ? $"{Session} ({User}, {state})"
            : $"{Session} ({User}, {Seat}, {state})";
    }
}

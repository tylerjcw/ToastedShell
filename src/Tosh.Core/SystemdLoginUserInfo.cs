namespace Tosh.Core;

public sealed record SystemdLoginUserInfo(
    int UserId,
    string User,
    bool Linger,
    string? State)
{
    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(State)
            ? $"{User} ({UserId})"
            : $"{User} ({UserId}, {State})";
    }
}

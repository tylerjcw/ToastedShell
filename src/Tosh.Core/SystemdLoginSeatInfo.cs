namespace Tosh.Core;

public sealed record SystemdLoginSeatInfo(string Seat)
{
    public override string ToString() => Seat;
}

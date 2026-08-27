namespace Tosh.Runtime;

public sealed record SystemdLoginSeatInfo(string Seat)
{
    public override string ToString() => Seat;
}

namespace Tosh.Core;

public sealed record SystemCounterInfo(string Counter, long Value)
{
    public override string ToString() => $"{Counter}: {Value}";
}

namespace Tosh.Runtime;

public sealed record SystemCounterInfo(string Counter, long Value)
{
    public override string ToString() => $"{Counter}: {Value}";
}

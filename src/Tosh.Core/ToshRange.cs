namespace Tosh.Core;

public sealed record ToshRange(int Start, int? Step, int? End) : IShellEnumerableObject
{
    public bool IsInfinite => End is null;

    public bool IsEmpty => End is not null && (Step > 0 ? Start > End : Step < 0 ? Start < End : true);

    public int Count
    {
        get
        {
            if (End is null) return int.MaxValue; // infinite
            var step = Step ?? 1;
            if (step == 0) return 0;
            if (step > 0 && Start > End) return 0;
            if (step < 0 && Start < End) return 0;
            return ((End.Value - Start) / step) + 1;
        }
    }

    public IEnumerable<int> Enumerate()
    {
        if (End is null)
        {
            var step = Step ?? 1;
            if (step == 0) yield break;
            for (long i = Start; ; i += step)
            {
                if (i > int.MaxValue || i < int.MinValue) yield break;
                yield return (int)i;
            }
        }
        else
        {
            var end = End.Value;
            var step = Step ?? (Start <= end ? 1 : -1);

            if (step > 0)
            {
                for (var i = Start; i <= end; i += step)
                {
                    yield return i;
                }
            }
            else if (step < 0)
            {
                for (var i = Start; i >= end; i += step)
                {
                    yield return i;
                }
            }
        }
    }

    public IEnumerable<object?> EnumerateShellItems()
    {
        foreach (var value in Enumerate())
        {
            yield return value;
        }
    }

    public override string ToString()
    {
        if (End is null)
            return Step is int s ? $"{Start}..{s}.." : $"{Start}..";
        return Step is int s2 ? $"{Start}..{s2}..{End}" : $"{Start}..{End}";
    }
}

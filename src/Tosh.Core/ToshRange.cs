namespace Tosh.Core;

public sealed record ToshRange(int Start, int? Step, int End) : IShellEnumerableObject
{
    public bool IsEmpty => Step > 0 ? Start > End : Step < 0 ? Start < End : true;

    public int Count
    {
        get
        {
            var step = Step ?? 1;
            if (step == 0) return 0;
            if (step > 0 && Start > End) return 0;
            if (step < 0 && Start < End) return 0;
            return ((End - Start) / step) + 1;
        }
    }

    public IEnumerable<int> Enumerate()
    {
        var step = Step ?? (Start <= End ? 1 : -1);

        if (step > 0)
        {
            for (var i = Start; i <= End; i += step)
            {
                yield return i;
            }
        }
        else if (step < 0)
        {
            for (var i = Start; i >= End; i += step)
            {
                yield return i;
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
        return Step is int s ? $"{Start}..{s}..{End}" : $"{Start}..{End}";
    }
}

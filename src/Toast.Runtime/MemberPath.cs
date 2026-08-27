namespace Tosh.Runtime;

public sealed record MemberPath(IReadOnlyList<MemberPathSegment> Segments)
{
    public bool IsNullable => Segments.Any(segment => segment.IsNullable);

    public static MemberPath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var segments = value
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new InvalidOperationException("A member path must contain at least one segment.");
        }

        return new MemberPath(segments);
    }

    private static MemberPathSegment ParseSegment(string value)
    {
        var isNullable = value.EndsWith("?", StringComparison.Ordinal);
        var name = isNullable ? value[..^1] : value;

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"Invalid member path segment '{value}'.");
        }

        return new MemberPathSegment(name, isNullable);
    }
}

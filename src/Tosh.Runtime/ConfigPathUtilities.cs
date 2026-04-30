namespace Tosh.Runtime;

public static class ConfigPathUtilities
{
    public static string NormalizeMemberPath(object root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        object? current = root;
        var normalizedSegments = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            var normalizedSegment = TryResolveSegmentName(current, segment) ?? segment;
            normalizedSegments.Add(normalizedSegment);
            current = current is null ? null : ShellRecordUtilities.TryGetValue(current, normalizedSegment, out var recordValue)
                ? recordValue
                : RuntimeGetValue(current, normalizedSegment);
        }

        return string.Join('.', normalizedSegments);
    }

    public static string? TryResolveSegmentName(object? current, string rawSegment)
    {
        if (current is null)
        {
            return null;
        }

        var normalizedRaw = NormalizeName(rawSegment);
        var type = current.GetType();

        var property = type
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(candidate => NormalizeName(candidate.Name) == normalizedRaw && candidate.GetIndexParameters().Length == 0);

        if (property is not null)
        {
            return property.Name;
        }

        var field = type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(candidate => NormalizeName(candidate.Name) == normalizedRaw);

        return field?.Name;
    }

    public static string NormalizeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(text.Where(character => char.IsLetterOrDigit(character))).ToLowerInvariant();
    }

    private static object? RuntimeGetValue(object target, string segment)
    {
        try
        {
            return new ReflectionObjectAccessor().GetValue(target, segment);
        }
        catch
        {
            return null;
        }
    }
}

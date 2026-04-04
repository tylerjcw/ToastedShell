namespace Tosh.Core;

public sealed class DisplayColumnSelection : IEquatable<DisplayColumnSelection>
{
    private readonly HashSet<string> _showLookup;
    private readonly HashSet<string> _hideLookup;

    public DisplayColumnSelection(IEnumerable<string>? showColumns = null, IEnumerable<string>? hideColumns = null, bool showAll = false)
    {
        var show = Normalize(showColumns);
        var hide = Normalize(hideColumns);

        ShowColumns = show;
        HideColumns = hide;
        ShowAll = showAll;
        _showLookup = new HashSet<string>(show, StringComparer.OrdinalIgnoreCase);
        _hideLookup = new HashSet<string>(hide, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> ShowColumns { get; }

    public IReadOnlyList<string> HideColumns { get; }

    public bool ShowAll { get; }

    public bool HasOverrides => ShowAll || ShowColumns.Count > 0 || HideColumns.Count > 0;

    public bool IncludesShownName(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        return _showLookup.Contains(candidate);
    }

    public bool IncludesHiddenName(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        return _hideLookup.Contains(candidate);
    }

    public bool Equals(DisplayColumnSelection? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null ||
            ShowAll != other.ShowAll ||
            ShowColumns.Count != other.ShowColumns.Count ||
            HideColumns.Count != other.HideColumns.Count)
        {
            return false;
        }

        for (var index = 0; index < ShowColumns.Count; index++)
        {
            if (!string.Equals(ShowColumns[index], other.ShowColumns[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return _hideLookup.SetEquals(other.HideColumns);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as DisplayColumnSelection);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ShowAll);

        foreach (var column in ShowColumns)
        {
            hash.Add(column, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var column in HideColumns.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(column, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();

            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }
}

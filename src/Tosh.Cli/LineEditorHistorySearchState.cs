namespace Tosh.Cli;

internal sealed class LineEditorHistorySearchState
{
    private readonly IReadOnlyList<string> _history;
    private readonly string _originalText;
    private readonly int _originalCursorIndex;

    public LineEditorHistorySearchState(IReadOnlyList<string> history, string originalText, int originalCursorIndex)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _originalText = originalText ?? string.Empty;
        _originalCursorIndex = Math.Clamp(originalCursorIndex, 0, _originalText.Length);
    }

    public string Query { get; private set; } = string.Empty;

    public string? MatchText { get; private set; }

    public int? MatchIndex { get; private set; }

    public bool HasMatch => MatchIndex.HasValue;

    public bool Failed { get; private set; }

    public void Activate(LineEditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        RefreshLatest(buffer);
    }

    public bool Append(LineEditorBuffer buffer, char character)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Query += character;
        RefreshLatest(buffer);
        return true;
    }

    public bool Backspace(LineEditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (Query.Length == 0)
        {
            return false;
        }

        Query = Query[..^1];
        RefreshLatest(buffer);
        return true;
    }

    public bool TryCyclePrevious(LineEditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var searchStart = MatchIndex ?? _history.Count;

        if (!TryFindMatch(searchStart, out var matchIndex, out var matchText))
        {
            return false;
        }

        ApplyMatch(buffer, matchIndex, matchText);
        return true;
    }

    public void Cancel(LineEditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        RestoreOriginal(buffer);
    }

    private void RefreshLatest(LineEditorBuffer buffer)
    {
        if (TryFindMatch(_history.Count, out var matchIndex, out var matchText))
        {
            ApplyMatch(buffer, matchIndex, matchText);
            return;
        }

        MatchIndex = null;
        MatchText = null;
        Failed = Query.Length > 0;
        RestoreOriginal(buffer);
    }

    private bool TryFindMatch(int startExclusiveIndex, out int matchIndex, out string matchText)
    {
        matchIndex = -1;
        matchText = string.Empty;

        var normalizedStart = Math.Clamp(startExclusiveIndex, 0, _history.Count);

        for (var index = normalizedStart - 1; index >= 0; index--)
        {
            var entry = _history[index];

            if (Query.Length == 0 || entry.Contains(Query, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = index;
                matchText = entry;
                return true;
            }
        }

        return false;
    }

    private void ApplyMatch(LineEditorBuffer buffer, int matchIndex, string matchText)
    {
        MatchIndex = matchIndex;
        MatchText = matchText;
        Failed = false;
        buffer.SetText(matchText);
        buffer.SetCursor(matchText.Length);
    }

    private void RestoreOriginal(LineEditorBuffer buffer)
    {
        buffer.SetText(_originalText);
        buffer.SetCursor(_originalCursorIndex);
    }
}

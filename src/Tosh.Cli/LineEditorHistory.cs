namespace Tosh.Cli;

public sealed class LineEditorHistory
{
    private readonly IReadOnlyList<string> _entries;
    private int _navigationIndex = -1;
    private string _pendingText = string.Empty;

    public LineEditorHistory(IReadOnlyList<string> entries)
    {
        _entries = entries;
    }

    public bool TryPrevious(string currentText, out string text)
    {
        if (_entries.Count == 0)
        {
            text = currentText;
            return false;
        }

        if (_navigationIndex == -1)
        {
            _pendingText = currentText;
            _navigationIndex = _entries.Count;
        }

        if (_navigationIndex == 0)
        {
            text = _entries[0];
            return true;
        }

        _navigationIndex--;
        text = _entries[_navigationIndex];
        return true;
    }

    public bool TryNext(out string text)
    {
        if (_navigationIndex == -1)
        {
            text = _pendingText;
            return false;
        }

        if (_navigationIndex < _entries.Count - 1)
        {
            _navigationIndex++;
            text = _entries[_navigationIndex];
            return true;
        }

        _navigationIndex = -1;
        text = _pendingText;
        return true;
    }
}

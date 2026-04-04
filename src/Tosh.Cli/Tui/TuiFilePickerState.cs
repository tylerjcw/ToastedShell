namespace Tosh.Cli.Tui;

internal enum TuiFilePickerSelectionMode
{
    Any,
    File,
    Directory,
}

internal enum TuiFilePickerResultKind
{
    None,
    Selected,
    Cancelled,
}

internal readonly record struct TuiFilePickerResult(
    TuiFilePickerResultKind Kind,
    string? Path = null);

internal sealed class TuiFilePickerState
{
    private readonly TuiListState<TuiFilePickerEntry> _entries = new();

    public bool IsOpen { get; private set; }

    public string CurrentDirectory { get; private set; } = string.Empty;

    public TuiFilePickerSelectionMode SelectionMode { get; private set; }

    public string? StatusMessage { get; private set; }

    public void Open(string startDirectory, TuiFilePickerSelectionMode selectionMode, string? initialSelectionPath, int pageSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        CurrentDirectory = Directory.Exists(startDirectory)
            ? startDirectory
            : Path.GetDirectoryName(startDirectory) ?? Environment.CurrentDirectory;
        SelectionMode = selectionMode;
        StatusMessage = null;
        IsOpen = true;
        Refresh(pageSize, initialSelectionPath);
    }

    public void Close()
    {
        IsOpen = false;
        StatusMessage = null;
        _entries.SetItems(Array.Empty<TuiFilePickerEntry>(), 1);
    }

    public TuiFilePickerResult HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        if (!IsOpen)
        {
            return new TuiFilePickerResult(TuiFilePickerResultKind.None);
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _entries.MovePrevious();
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.DownArrow:
                _entries.MoveNext();
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.PageUp:
                _entries.PageUp();
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.PageDown:
                _entries.PageDown();
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.Home:
                _entries.Home();
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.End:
                _entries.End();
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.LeftArrow:
            case ConsoleKey.Backspace:
                NavigateToParent(pageSize);
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.RightArrow:
                EnterSelectedDirectory(pageSize);
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case ConsoleKey.Spacebar:
                return TrySelectHighlightedPath();
            case ConsoleKey.Enter:
                return HandleEnter(pageSize);
            case ConsoleKey.Escape:
                return new TuiFilePickerResult(TuiFilePickerResultKind.Cancelled);
            default:
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
        }
    }

    public IReadOnlyList<string> BuildEntries(int width, int height)
    {
        var entries = new List<string>
        {
            $"Location: {CurrentDirectory}",
            $"Mode: {SelectionMode}",
        };

        if (!string.IsNullOrWhiteSpace(StatusMessage))
        {
            entries.Add($"Status: {StatusMessage}");
        }

        entries.Add(string.Empty);

        var pageSize = Math.Max(1, height - 7);
        _entries.SetItems(_entries.Items, pageSize);
        var range = _entries.Scroll.GetVisibleRange();

        for (var row = 0; row < range.Length; row++)
        {
            var itemIndex = range.Start + row;
            var item = _entries.Items[itemIndex];
            var prefix = itemIndex == _entries.SelectedIndex ? ">" : " ";
            entries.Add($"{prefix} {item.Label}");
        }

        entries.Add(string.Empty);
        entries.AddRange(TextDocumentFormatter.WrapParagraph(
            "Up and Down move through entries. Right enters a directory. Left or Backspace goes to the parent. Enter opens directories or selects files. Space selects the highlighted path directly.",
            width));

        return entries;
    }

    private TuiFilePickerResult HandleEnter(int pageSize)
    {
        if (!_entries.TryGetSelected(out var entry))
        {
            return new TuiFilePickerResult(TuiFilePickerResultKind.None);
        }

        switch (entry.Kind)
        {
            case TuiFilePickerEntryKind.CurrentDirectory:
                return SelectionMode == TuiFilePickerSelectionMode.File
                    ? new TuiFilePickerResult(TuiFilePickerResultKind.None)
                    : new TuiFilePickerResult(TuiFilePickerResultKind.Selected, CurrentDirectory);
            case TuiFilePickerEntryKind.ParentDirectory:
                NavigateToParent(pageSize);
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case TuiFilePickerEntryKind.Directory:
                if (SelectionMode == TuiFilePickerSelectionMode.Directory)
                {
                    return new TuiFilePickerResult(TuiFilePickerResultKind.Selected, entry.FullPath);
                }

                NavigateTo(entry.FullPath, pageSize, preferredPath: null);
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
            case TuiFilePickerEntryKind.File:
                return SelectionMode == TuiFilePickerSelectionMode.Directory
                    ? new TuiFilePickerResult(TuiFilePickerResultKind.None)
                    : new TuiFilePickerResult(TuiFilePickerResultKind.Selected, entry.FullPath);
            default:
                return new TuiFilePickerResult(TuiFilePickerResultKind.None);
        }
    }

    private TuiFilePickerResult TrySelectHighlightedPath()
    {
        if (!_entries.TryGetSelected(out var entry))
        {
            return new TuiFilePickerResult(TuiFilePickerResultKind.None);
        }

        return entry.Kind switch
        {
            TuiFilePickerEntryKind.CurrentDirectory when SelectionMode != TuiFilePickerSelectionMode.File
                => new TuiFilePickerResult(TuiFilePickerResultKind.Selected, CurrentDirectory),
            TuiFilePickerEntryKind.Directory when SelectionMode != TuiFilePickerSelectionMode.File
                => new TuiFilePickerResult(TuiFilePickerResultKind.Selected, entry.FullPath),
            TuiFilePickerEntryKind.File when SelectionMode != TuiFilePickerSelectionMode.Directory
                => new TuiFilePickerResult(TuiFilePickerResultKind.Selected, entry.FullPath),
            _ => new TuiFilePickerResult(TuiFilePickerResultKind.None),
        };
    }

    private void EnterSelectedDirectory(int pageSize)
    {
        if (!_entries.TryGetSelected(out var entry))
        {
            return;
        }

        if (entry.Kind == TuiFilePickerEntryKind.ParentDirectory)
        {
            NavigateToParent(pageSize);
            return;
        }

        if (entry.Kind != TuiFilePickerEntryKind.Directory)
        {
            return;
        }

        NavigateTo(entry.FullPath, pageSize, preferredPath: null);
    }

    private void NavigateToParent(int pageSize)
    {
        var parent = Directory.GetParent(CurrentDirectory);

        if (parent is null)
        {
            return;
        }

        NavigateTo(parent.FullName, pageSize, preferredPath: CurrentDirectory);
    }

    private void NavigateTo(string directory, int pageSize, string? preferredPath)
    {
        CurrentDirectory = directory;
        Refresh(pageSize, preferredPath);
    }

    private void Refresh(int pageSize, string? preferredPath)
    {
        var items = new List<TuiFilePickerEntry>();

        if (SelectionMode != TuiFilePickerSelectionMode.File)
        {
            items.Add(new TuiFilePickerEntry(TuiFilePickerEntryKind.CurrentDirectory, CurrentDirectory, "[.] Select this directory"));
        }

        var parent = Directory.GetParent(CurrentDirectory);

        if (parent is not null)
        {
            items.Add(new TuiFilePickerEntry(TuiFilePickerEntryKind.ParentDirectory, parent.FullName, "[..] Parent directory"));
        }

        try
        {
            var directories = Directory.EnumerateDirectories(CurrentDirectory)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new TuiFilePickerEntry(
                    TuiFilePickerEntryKind.Directory,
                    path,
                    $"[/] {Path.GetFileName(path)}/"));
            items.AddRange(directories);

            var files = Directory.EnumerateFiles(CurrentDirectory)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new TuiFilePickerEntry(
                    TuiFilePickerEntryKind.File,
                    path,
                    $"[-] {Path.GetFileName(path)}"));
            items.AddRange(files);

            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }

        _entries.SetItems(items, Math.Max(1, pageSize));

        if (preferredPath is not null)
        {
            var preferredIndex = items.FindIndex(item => string.Equals(item.FullPath, preferredPath, StringComparison.OrdinalIgnoreCase));

            if (preferredIndex >= 0)
            {
                _entries.SelectIndex(preferredIndex);
                return;
            }
        }

        _entries.SelectIndex(0);
    }

    private sealed record TuiFilePickerEntry(TuiFilePickerEntryKind Kind, string FullPath, string Label);

    private enum TuiFilePickerEntryKind
    {
        CurrentDirectory,
        ParentDirectory,
        Directory,
        File,
    }
}

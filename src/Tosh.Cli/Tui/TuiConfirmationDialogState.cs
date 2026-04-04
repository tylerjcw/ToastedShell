namespace Tosh.Cli.Tui;

internal enum TuiConfirmationDialogResultKind
{
    None,
    Confirmed,
    Cancelled,
}

internal readonly record struct TuiConfirmationDialogResult(
    TuiConfirmationDialogResultKind Kind,
    string? ConfirmLabel = null,
    string? CancelLabel = null);

internal sealed class TuiConfirmationDialogState
{
    public bool IsOpen { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public string ConfirmLabel { get; private set; } = "Confirm";

    public string CancelLabel { get; private set; } = "Cancel";

    public bool ConfirmSelected { get; private set; } = true;

    public void Open(
        string title,
        string message,
        string confirmLabel = "Confirm",
        string cancelLabel = "Cancel",
        bool confirmSelected = true)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
        ConfirmSelected = confirmSelected;
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
    }

    public TuiConfirmationDialogResult HandleKey(ConsoleKeyInfo key)
    {
        if (!IsOpen)
        {
            return new TuiConfirmationDialogResult(TuiConfirmationDialogResultKind.None);
        }

        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
            case ConsoleKey.Tab:
                ConfirmSelected = !ConfirmSelected;
                return new TuiConfirmationDialogResult(TuiConfirmationDialogResultKind.None);
            case ConsoleKey.Enter:
                return ConfirmSelected
                    ? new TuiConfirmationDialogResult(TuiConfirmationDialogResultKind.Confirmed, ConfirmLabel, CancelLabel)
                    : new TuiConfirmationDialogResult(TuiConfirmationDialogResultKind.Cancelled, ConfirmLabel, CancelLabel);
            case ConsoleKey.Y:
            case ConsoleKey.Q:
                return new TuiConfirmationDialogResult(TuiConfirmationDialogResultKind.Confirmed, ConfirmLabel, CancelLabel);
            case ConsoleKey.N:
            case ConsoleKey.Escape:
                return new TuiConfirmationDialogResult(TuiConfirmationDialogResultKind.Cancelled, ConfirmLabel, CancelLabel);
            default:
                return new TuiConfirmationDialogResult(TuiConfirmationDialogResultKind.None);
        }
    }

    public IReadOnlyList<string> BuildEntries(int width)
    {
        var entries = new List<string>();
        entries.AddRange(TextDocumentFormatter.WrapParagraph(Message, width));
        entries.Add(string.Empty);
        entries.Add($"{(ConfirmSelected ? ">" : " ")} [{ConfirmLabel}]    {(!ConfirmSelected ? ">" : " ")} [{CancelLabel}]");
        entries.Add(string.Empty);
        entries.Add("Left/Right or Tab switches the selection. Enter confirms. Esc cancels.");
        return entries;
    }
}

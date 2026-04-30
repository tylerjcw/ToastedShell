namespace Tosh.Runtime;

public enum DisplayTableAlignment
{
    Left,
    Right,
}

public sealed record DisplayTableColumn(
    string Header,
    Func<object, object?> ValueAccessor,
    DisplayTableAlignment Alignment = DisplayTableAlignment.Left,
    int MinWidth = 4,
    int MaxWidth = 36,
    int Priority = 100,
    bool CanHide = true,
    string? SelectionKey = null,
    bool IsTree = false,
    bool UseHeaderTheme = true,
    bool UseIndexTheme = true);

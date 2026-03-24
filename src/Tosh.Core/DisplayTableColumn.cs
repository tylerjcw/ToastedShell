namespace Tosh.Core;

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
    bool CanHide = true);

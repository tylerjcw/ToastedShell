namespace Tosh.Core;

[Flags]
public enum DisplaySurface
{
    Root = 1,
    Nested = 2,
    TableCell = 4,
    Any = Root | Nested | TableCell,
}

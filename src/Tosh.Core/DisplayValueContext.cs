namespace Tosh.Core;

public sealed record DisplayValueContext(
    object Value,
    DisplaySurface Surface,
    ObjectRenderStyle Style,
    DisplayRenderOptions? RenderOptions = null,
    ObjectFormattingOptions? FormattingOptions = null)
{
    public Type ValueType => Value.GetType();
}

using System.Drawing;
using System.Globalization;
using System.Security;
using System.Text;

namespace Tosh.Stdlib.Plotting;

public sealed class SvgCanvas
{
    private readonly StringBuilder _sb = new();
    private int _clipIdCounter = 0;
    private int _clipDepth = 0;

    public SvgCanvas(int width, int height, string? title = null)
    {
        _sb.Append($"""<svg xmlns="http://www.w3.org/2000/svg" role="img" width="{width}" height="{height}" viewBox="0 0 {width} {height}" style="background-color: transparent; font-family: system-ui, -apple-system, sans-serif;">""");
        if (!string.IsNullOrEmpty(title))
        {
            _sb.Append($"<title>{Escape(title)}</title>");
        }
    }

    public static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;
    private static string N(float val) => val.ToString("0.##", CultureInfo.InvariantCulture);
    private static string N(double val) => val.ToString("0.##", CultureInfo.InvariantCulture);

    public void Rect(RectangleF rect, Color fill, Color? stroke = null, float strokeWidth = 1f)
    {
        var strokeAttr = stroke.HasValue && stroke.Value.A > 0
            ? $" stroke='{stroke.Value.ToSvgHex()}' stroke-width='{N(strokeWidth)}'"
            : "";
        _sb.Append($"<rect x='{N(rect.X)}' y='{N(rect.Y)}' width='{N(rect.Width)}' height='{N(rect.Height)}' fill='{fill.ToSvgHex()}'{strokeAttr}/>");
    }

    public void Circle(PointF center, float radius, Color fill, Color? stroke = null, float strokeWidth = 1f)
    {
        var strokeAttr = stroke.HasValue && stroke.Value.A > 0
            ? $" stroke='{stroke.Value.ToSvgHex()}' stroke-width='{N(strokeWidth)}'"
            : "";
        _sb.Append($"<circle cx='{N(center.X)}' cy='{N(center.Y)}' r='{N(radius)}' fill='{fill.ToSvgHex()}'{strokeAttr}/>");
    }

    public void Line(PointF p1, PointF p2, Color stroke, float strokeWidth = 1f, string? strokeDash = null)
    {
        var dashAttr = string.IsNullOrEmpty(strokeDash) ? "" : $" stroke-dasharray='{strokeDash}'";
        _sb.Append($"<line x1='{N(p1.X)}' y1='{N(p1.Y)}' x2='{N(p2.X)}' y2='{N(p2.Y)}' stroke='{stroke.ToSvgHex()}' stroke-width='{N(strokeWidth)}'{dashAttr}/>");
    }

    public void Path(IEnumerable<PointF> points, Color stroke, float strokeWidth = 2f, string? strokeDash = null)
    {
        var pathData = new StringBuilder();
        bool first = true;
        foreach (var pt in points)
        {
            if (float.IsNaN(pt.X) || float.IsNaN(pt.Y)) continue;
            pathData.Append(first ? "M" : " L").Append(N(pt.X)).Append(" ").Append(N(pt.Y));
            first = false;
        }

        if (first) return;
        var dashAttr = string.IsNullOrEmpty(strokeDash) ? "" : $" stroke-dasharray='{strokeDash}'";
        _sb.Append($"<path d='{pathData}' fill='none' stroke='{stroke.ToSvgHex()}' stroke-width='{N(strokeWidth)}' stroke-linejoin='round' stroke-linecap='round'{dashAttr}/>");
    }

    public void Polygon(IEnumerable<PointF> points, Color fill)
    {
        var pathData = new StringBuilder();
        int count = 0;
        foreach (var pt in points)
        {
            if (float.IsNaN(pt.X) || float.IsNaN(pt.Y)) continue;
            pathData.Append(count == 0 ? "M" : " L").Append(N(pt.X)).Append(" ").Append(N(pt.Y));
            count++;
        }

        if (count < 3) return;
        _sb.Append($"<path d='{pathData} Z' fill='{fill.ToSvgHex()}' stroke='none' fill-rule='evenodd'/>");
    }

    public void Text(PointF pt, string text, float fontSize, string anchor, Color color, float rotation = 0f, bool bold = false)
    {
        var tx = N(pt.X);
        var ty = N(pt.Y);
        var weight = bold ? " font-weight='bold'" : "";
        var transform = rotation != 0f ? $" transform='rotate({N(rotation)} {tx} {ty})'" : "";
        _sb.Append($"<text x='{tx}' y='{ty}' font-size='{N(fontSize)}' text-anchor='{anchor}' fill='{color.ToSvgHex()}'{weight}{transform}>{Escape(text)}</text>");
    }

    public void BeginClip(RectangleF rect)
    {
        _clipIdCounter++;
        _clipDepth++;
        var id = $"plot-clip-{_clipIdCounter}";
        _sb.Append($"<defs><clipPath id='{id}'><rect x='{N(rect.X)}' y='{N(rect.Y)}' width='{N(rect.Width)}' height='{N(rect.Height)}'/></clipPath></defs><g clip-path='url(#{id})'>");
    }

    public void EndClip()
    {
        if (_clipDepth <= 0) return;
        _clipDepth--;
        _sb.Append("</g>");
    }

    public string Finish()
    {
        while (_clipDepth > 0)
        {
            EndClip();
        }
        _sb.Append("</svg>");
        return _sb.ToString();
    }
}

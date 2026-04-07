namespace Tosh.LanguageServices;

public sealed class TextCoordinateMap
{
    private readonly string _text;
    private readonly int[] _lineStarts;

    public TextCoordinateMap(string text)
    {
        _text = text ?? string.Empty;
        _lineStarts = BuildLineStarts(_text);
    }

    public int Length => _text.Length;

    public LspPosition ToPosition(int offset)
    {
        var boundedOffset = Math.Clamp(offset, 0, _text.Length);
        var line = Array.BinarySearch(_lineStarts, boundedOffset);

        if (line < 0)
        {
            line = ~line - 1;
        }

        line = Math.Clamp(line, 0, _lineStarts.Length - 1);
        return new LspPosition(line, boundedOffset - _lineStarts[line]);
    }

    public int ToOffset(LspPosition position)
    {
        var line = Math.Clamp(position.Line, 0, _lineStarts.Length - 1);
        var lineStart = _lineStarts[line];
        var lineEnd = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;
        return Math.Clamp(lineStart + position.Character, lineStart, lineEnd);
    }

    public LspRange ToRange(int startOffset, int endOffset)
    {
        return new LspRange(ToPosition(startOffset), ToPosition(endOffset));
    }

    public static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return starts.ToArray();
    }
}

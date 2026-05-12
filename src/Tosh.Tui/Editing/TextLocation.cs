namespace Tosh.Tui.Editing;

/// <summary>A line+column position within a <see cref="TextBuffer"/>. Zero-based.</summary>
public readonly record struct TextLocation(int Line, int Column)
{
    public static TextLocation Zero => default;

    public override string ToString() => $"{Line + 1}:{Column + 1}";
}

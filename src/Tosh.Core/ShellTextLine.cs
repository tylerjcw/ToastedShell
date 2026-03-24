namespace Tosh.Core;

public sealed record ShellTextLine(string Text)
{
    public override string ToString() => Text;
}

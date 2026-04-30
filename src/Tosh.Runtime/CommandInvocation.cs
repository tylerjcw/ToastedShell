namespace Tosh.Runtime;

public sealed record CommandInvocation(
    string SourceName,
    string SourceText,
    string CommandName,
    TextSpan CommandSpan,
    IReadOnlyList<TextSpan> ArgumentSpans)
{
    public TextSpan? GetArgumentSpan(int index)
    {
        if (index < 0 || index >= ArgumentSpans.Count)
        {
            return null;
        }

        return ArgumentSpans[index];
    }
}

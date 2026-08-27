namespace Tosh.Runtime;

internal sealed record TextInputLine(
    string Text,
    string? Path,
    int LineNumber);

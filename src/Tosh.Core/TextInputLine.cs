namespace Tosh.Core;

internal sealed record TextInputLine(
    string Text,
    string? Path,
    int LineNumber);

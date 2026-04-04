namespace Tosh.Core;

public sealed record CommandDisplaySelectionParseResult(
    DisplayColumnSelection Selection,
    IReadOnlyList<object?> RemainingArguments);

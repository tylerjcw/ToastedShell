namespace Tosh.Runtime;

public sealed record CommandDisplaySelectionParseResult(
    DisplayColumnSelection Selection,
    IReadOnlyList<object?> RemainingArguments);

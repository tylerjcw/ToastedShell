namespace Tosh.Core.Commands;

public sealed class SortCommand : ShellCommand
{
    public SortCommand(string name = "sort")
        : base(name, "Sorts the current pipeline objects.", $"{name} [-r|--reverse] [member-path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count > 1)
        {
            throw new InvalidOperationException("The 'sort' command accepts at most one member-path argument.");
        }

        var reverse = parsed.HasFlag("r", "reverse", "desc", "descending");
        var memberPath = parsed.Positionals.Count == 0
            ? null
            : CommandArguments.RequireString(parsed.Positionals, 0, "member path");

        var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var comparer = new ShellSortComparer();
        var ordered = reverse
            ? items.OrderByDescending(item => memberPath is null ? item : context.Runtime.ObjectAccessor.GetValue(item, memberPath), comparer)
            : items.OrderBy(item => memberPath is null ? item : context.Runtime.ObjectAccessor.GetValue(item, memberPath), comparer);

        foreach (var item in ordered)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private sealed class ShellSortComparer : IComparer<object?>
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            if (x is string leftText && y is string rightText)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(leftText, rightText);
            }

            if (x is IComparable comparable &&
                TypeConversion.TryConvert(y, x.GetType(), out var convertedY))
            {
                return comparable.CompareTo(convertedY);
            }

            if (y is IComparable reverseComparable &&
                TypeConversion.TryConvert(x, y.GetType(), out var convertedX))
            {
                return -reverseComparable.CompareTo(convertedX);
            }

            var leftString = x.ToString() ?? x.GetType().Name;
            var rightString = y.ToString() ?? y.GetType().Name;
            return StringComparer.OrdinalIgnoreCase.Compare(leftString, rightString);
        }
    }
}

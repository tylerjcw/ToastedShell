namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandArgument("key", "A member path, callable, or block to extract the sort key.", Required = false, TypeName = "member-path|callable|block")]
[CommandOption("-r", "Reverse the sort order.")]
[CommandOption("-n", "Use numeric comparison.")]
[CommandOption("-u", "Remove duplicate values after sorting.")]
[CommandOption("-h", "Human-numeric sort (understands storage sizes).")]
[CommandExample("ps | sort Memory", Title = "Sort by property")]
[CommandExample("ls -la | sort Modified | reverse", Title = "Sort then reverse")]
[CommandExample("ps | sort func(p) => ($p.Name.Length)", Title = "Lambda sort key")]
[CommandOutput("The input pipeline objects in sorted order.")]
[PipelineInput(AcceptsScalar = true, Description = "Collects all pipeline objects, sorts them, then re-emits.")]
public sealed class SortCommand : ShellCommand
{
    public SortCommand(string name = "sort")
        : base(name, "Sorts the current pipeline objects.", $"{name} [-r|--reverse] [-n|--numeric] [-u|--unique] [-h|--human-numeric] [member-path|callable|block]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count > 1)
        {
            throw new InvalidOperationException("The 'sort' command accepts at most one member-path argument.");
        }

        var reverse = parsed.HasFlag("r", "d", "reverse", "desc", "descending");
        var numeric = parsed.HasFlag("n", "numeric");
        var unique = parsed.HasFlag("u", "unique");
        var humanNumeric = parsed.HasFlag("h", "human-numeric");
        var selector = parsed.Positionals.Count == 0 ? null : parsed.Positionals[0];

        if (parsed.Positionals.Count > 0 &&
            selector is not null &&
            selector is not string &&
            selector is not IShellCallable &&
            selector is not ShellBlock)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::sort_requires_selector",
                title: "'sort' selectors must be a member path, callable value, or block.",
                argumentIndex: 0,
                label: "this selector is not supported");
        }

        var comparer = new ShellSortComparer(numeric, humanNumeric);

        async Task<object?> SelectAsync(object? value)
        {
            if (selector is null)
            {
                return value;
            }

            if (selector is IShellCallable or ShellBlock)
            {
                return await FunctionalCommandUtilities.RequireSingleResultAsync(
                    context,
                    selector,
                    [value],
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["_"] = value,
                    });
            }

            var memberPath = CommandArguments.RequireString(parsed.Positionals, 0, "member path");
            return context.Runtime.ObjectAccessor.GetValue(value, memberPath);
        }

        async Task<TreeEntryInfo> SortTreeAsync(TreeEntryInfo node)
        {
            if (node.Children.Count == 0)
            {
                return node;
            }

            var keyedChildren = new List<(TreeEntryInfo Node, object? Key)>(node.Children.Count);

            foreach (var child in node.Children)
            {
                var sortedChild = await SortTreeAsync(child);
                var key = await SelectAsync(sortedChild);
                keyedChildren.Add((sortedChild, key));
            }

            var orderedChildren = reverse
                ? keyedChildren.OrderByDescending(entry => entry.Key, comparer)
                : keyedChildren.OrderBy(entry => entry.Key, comparer);

            return node with
            {
                Children = orderedChildren.Select(entry => entry.Node).ToArray(),
            };
        }

        var (tree, items) = await ShellIterationUtilities.PeekForTreeAsync(context.Input, context.CancellationToken);

        if (tree is not null)
        {
            yield return await SortTreeAsync(tree);
            yield break;
        }

        var collected = await AsyncEnumerableExtensions.ToListAsync(items, context.CancellationToken);
        var keyed = new List<(object? Item, object? Key)>(collected.Count);

        foreach (var item in collected)
        {
            object? key;

            if (selector is null)
            {
                key = item;
            }
            else if (selector is IShellCallable or ShellBlock)
            {
                key = await SelectAsync(item);
            }
            else
            {
                key = await SelectAsync(item);
            }

            keyed.Add((item, key));
        }

        var ordered = reverse
            ? keyed.OrderByDescending(entry => entry.Key, comparer)
            : keyed.OrderBy(entry => entry.Key, comparer);

        var seen = unique ? new HashSet<object?>(ShellEqualityComparer.Instance) : null;

        foreach (var entry in ordered)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (seen is not null)
            {
                if (!seen.Add(entry.Key))
                {
                    continue;
                }
            }

            yield return entry.Item;
        }
    }

    private sealed class ShellSortComparer : IComparer<object?>
    {
        private readonly bool _numeric;
        private readonly bool _humanNumeric;

        public ShellSortComparer(bool numeric = false, bool humanNumeric = false)
        {
            _numeric = numeric;
            _humanNumeric = humanNumeric;
        }

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

            if (_humanNumeric)
            {
                var xText = x.ToString() ?? string.Empty;
                var yText = y.ToString() ?? string.Empty;

                if (StorageSize.TryParse(xText, out var xSize) && StorageSize.TryParse(yText, out var ySize))
                {
                    return xSize.Bytes.CompareTo(ySize.Bytes);
                }
            }

            if (_numeric)
            {
                if (TryGetDouble(x, out var xNum) && TryGetDouble(y, out var yNum))
                {
                    return xNum.CompareTo(yNum);
                }
            }

            if (x is string leftText && y is string rightText)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(leftText, rightText);
            }

            // Try to convert y to x's type for comparison, but skip the string
            // target type since TryConvert to string always succeeds via ToString()
            // and would give misleading ordinal comparisons for non-string types.
            if (x is IComparable comparable && x is not string &&
                x.GetType() != typeof(string) &&
                TypeConversion.TryConvert(y, x.GetType(), out var convertedY))
            {
                return comparable.CompareTo(convertedY);
            }

            if (y is IComparable reverseComparable && y is not string &&
                y.GetType() != typeof(string) &&
                TypeConversion.TryConvert(x, y.GetType(), out var convertedX))
            {
                return -reverseComparable.CompareTo(convertedX);
            }

            // When types are incompatible, group by type name first for
            // a stable and consistent ordering, then compare within groups.
            var xTypeName = x.GetType().Name;
            var yTypeName = y.GetType().Name;

            if (!string.Equals(xTypeName, yTypeName, StringComparison.Ordinal))
            {
                return string.Compare(xTypeName, yTypeName, StringComparison.Ordinal);
            }

            var leftString = x.ToString() ?? xTypeName;
            var rightString = y.ToString() ?? yTypeName;
            return StringComparer.OrdinalIgnoreCase.Compare(leftString, rightString);
        }

        private static bool TryGetDouble(object value, out double result)
        {
            if (value is double d) { result = d; return true; }
            if (value is int i) { result = i; return true; }
            if (value is long l) { result = l; return true; }
            if (value is float f) { result = f; return true; }
            if (value is decimal m) { result = (double)m; return true; }

            if (value is string text && double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result))
            {
                return true;
            }

            result = 0;
            return false;
        }
    }

    private sealed class ShellEqualityComparer : IEqualityComparer<object?>
    {
        public static readonly ShellEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            if (x is string leftText && y is string rightText)
            {
                return StringComparer.OrdinalIgnoreCase.Equals(leftText, rightText);
            }

            return x.Equals(y);
        }

        public int GetHashCode(object? obj)
        {
            if (obj is null)
            {
                return 0;
            }

            if (obj is string text)
            {
                return StringComparer.OrdinalIgnoreCase.GetHashCode(text);
            }

            return obj.GetHashCode();
        }
    }
}

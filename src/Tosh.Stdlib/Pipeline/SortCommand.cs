using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("key", "A member path, callable, or block to extract the sort key.", Required = false, TypeName = "member-path|callable|block", Kind = "expression")]
[CommandOption("-r, --reverse", "Reverse the sort order.")]
[CommandOption("-d, --desc, --descending", "Alias for --reverse: sort in descending order.")]
[CommandOption("-n, --numeric", "Use numeric comparison.")]
[CommandOption("-u, --unique", "Remove duplicate values after sorting.")]
[CommandOption("-h, --human-numeric", "Human-numeric sort (understands storage sizes).")]
[CommandOption("-i, --ignore-case", "Order letters without regard to case.")]
[CommandOption("-o, --ordinal", "Compare by code point, case-sensitively. This is the default; accepted so existing scripts keep working.")]
[CommandExample("ps | sort Memory", Title = "Sort by property")]
[CommandExample("ls -la | sort Modified | reverse", Title = "Sort then reverse")]
[CommandExample("ps | sort func(p) => ($p.Name.Length)", Title = "Lambda sort key")]
[CommandExample("$names | sort -i", Title = "Case-insensitive order, for reading")]
[CommandOutput("The input pipeline objects in sorted order.")]
[PipelineInput(AcceptsScalar = true, Description = "Collects all pipeline objects, sorts them, then re-emits.")]
[CommandStreaming(StreamingBehavior.Eager)]
public sealed class SortCommand : ShellCommand
{
    public SortCommand(string name = "sort")
        : base(name, "Sorts the current pipeline objects.", $"{name} [-r|--reverse] [-n|--numeric] [-u|--unique] [-h|--human-numeric] [-i|--ignore-case] [-o|--ordinal] [member-path|callable|block]") { }

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

        // `TOAST-0018`. The default was `OrdinalIgnoreCase` — `Apple` and `apple` belong
        // next to each other when a person is reading — and it is now code point, with
        // `-i` to ask for the old behaviour. Two reasons, and the second is the decisive
        // one:
        //
        // `TS-P2-75` had already recorded the first: case folding raises lowercase
        // letters above `_`, so `expected_record_fields` sorted *before*
        // `expected_record_field_default` while by code point it sorts after. Anything
        // generated and committed wants code-point order.
        //
        // The second is that a case-insensitive order calls `"a"` and `"A"` **equal**,
        // while `==` calls them different. That is not a preference, it is a broken
        // trichotomy: two values neither less, nor greater, nor equal. The language's
        // ordering is by code point (§Ordering), and `sort` no longer contradicts it by
        // default.
        //
        // `-o`/`--ordinal` is still accepted and now names the default, so a script that
        // asked for code-point order keeps getting exactly what it asked for.
        var ignoreCase = parsed.HasFlag("i", "ignore-case");
        var selector = parsed.Positionals.Count == 0 ? null : parsed.Positionals[0];

        if (parsed.Positionals.Count > 0 &&
            selector is not null &&
            selector is not string &&
            selector is not IShellCallable &&
            selector is not ShellBlock)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.sort_requires_selector",
                title: "'sort' selectors must be a member path, callable value, or block.",
                argumentIndex: 0,
                label: "this selector is not supported");
        }

        var comparer = new ShellSortComparer(numeric, humanNumeric, ignoreCase);

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

        // `TOAST-0018`. Case-sensitive uniqueness is the shared key relation, so `-u`
        // and a dictionary agree about which values are the same. `-i` keeps its own
        // comparer, because folding case is a `sort` option and not a property of a key.
        var seen = unique
            ? new HashSet<object?>(ignoreCase ? ShellEqualityComparer.Instance : (IEqualityComparer<object?>)ShellKeyComparer.Instance)
            : null;

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

    private sealed class ShellEqualityComparer : IEqualityComparer<object?>
    {
        public static readonly ShellEqualityComparer Instance = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The `--unique` counterpart of `--ordinal` (`TS-P2-75`). Deduplication has to
        /// agree with the comparison it follows, or `-u -o` would fold `Alpha` and
        /// `alpha` together while the sort had just placed them apart.
        /// </summary>
        public static readonly ShellEqualityComparer Ordinal = new(StringComparer.Ordinal);

        private readonly StringComparer _strings;

        private ShellEqualityComparer(StringComparer strings) => _strings = strings;

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
                return _strings.Equals(leftText, rightText);
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
                return _strings.GetHashCode(text);
            }

            return obj.GetHashCode();
        }
    }
}

using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandExample("ls -la | get Name", Title = "Pluck a single field")]
[CommandExample("ls | get Name Size Modified", Title = "Project multiple fields (variadic)")]
[CommandExample("ps | get { Name, PID, Memory }", Title = "Project multiple fields with brace syntax")]
[CommandExample("echo 1 2 3 | get func(x) => ($x * 2)", Title = "Project with a function")]
[CommandNote("`get` is the column-picker (member values). For row picking by index/range/list, use `row`.")]
[CommandOutput("The selected member value(s) — one item per requested path, in input order.")]
public sealed class GetCommand : ShellCommand
{
    public GetCommand(string name = "get")
        : base(name, "Gets a member or projects fields from each pipeline item.", $"{name} <member-path> or {name} <m1> <m2> ... or {name} {{ <member-path>, ... }}") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("Missing required argument: member path.");
        }

        // Variadic field projection: get Name Size Email
        // Triggered when 2+ args and every arg is a string (bareword identifier).
        if (context.Arguments.Count >= 2 && context.Arguments.All(arg => arg is string))
        {
            var paths = context.Arguments.Cast<string>().ToList();
            var variadic = new ProjectedMemberSelection(paths);

            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                yield return Project(item, variadic, context.Runtime.ObjectAccessor);
            }

            yield break;
        }

        if (context.Arguments[0] is IShellCallable callable)
        {
            await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                               .WithCancellation(context.CancellationToken))
            {
                yield return await FunctionalCommandUtilities.RequireSingleResultAsync(
                    context,
                    callable,
                    [item],
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["_"] = item,
                    });
            }

            yield break;
        }

        // Range slice: get 2..5
        if (context.Arguments[0] is ToshRange range)
        {
            var current = 0;
            var start = range.Start;
            var end = range.End;
            var step = range.Step ?? 1;
            var (tree, input) = await ShellIterationUtilities.PeekForTreeAsync(context.Input, context.CancellationToken);

            if (tree is not null)
            {
                input = WrapTreeFlat(tree);
            }

            if (end is null && step > 0)
            {
                // Open-ended range: get 2.. means "skip first 2, yield rest"
                await foreach (var item in input.WithCancellation(context.CancellationToken))
                {
                    if (current >= start)
                    {
                        yield return item;
                    }

                    current++;
                }
            }
            else if (step == 1 && start <= (end ?? int.MaxValue))
            {
                var endValue = end ?? int.MaxValue;
                // Contiguous range — simple and fast.
                await foreach (var item in input.WithCancellation(context.CancellationToken))
                {
                    if (current >= start && current <= endValue)
                    {
                        yield return item;
                    }

                    if (current > endValue)
                    {
                        yield break;
                    }

                    current++;
                }
            }
            else
            {
                // Stepped or reverse range — collect indices first.
                var indices = new HashSet<int>(range.Enumerate());

                if (indices.Count == 0)
                {
                    yield break;
                }

                var maxIndex = indices.Max();

                await foreach (var item in input.WithCancellation(context.CancellationToken))
                {
                    if (indices.Contains(current))
                    {
                        yield return item;
                    }

                    if (current > maxIndex)
                    {
                        yield break;
                    }

                    current++;
                }
            }

            yield break;
        }

        // Index access: get 4
        if (TryGetIndex(context.Arguments[0], out var index))
        {
            var current = 0;
            var (tree, input) = await ShellIterationUtilities.PeekForTreeAsync(context.Input, context.CancellationToken);

            if (tree is not null)
            {
                input = WrapTreeFlat(tree);
            }

            await foreach (var item in input.WithCancellation(context.CancellationToken))
            {
                if (current == index)
                {
                    yield return item;
                    yield break;
                }

                current++;
            }

            throw context.CreateDiagnostic(
                "tosh.get.index_out_of_range",
                $"Index {index} is out of range (pipeline had {current} items).",
                argumentIndex: 0,
                label: "this index is past the end");
        }

        // Multi-member projection: get { Name, Size }
        if (context.Arguments[0] is ProjectedMemberSelection selection)
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                yield return Project(item, selection, context.Runtime.ObjectAccessor);
            }

            yield break;
        }

        // Single member access: get Name
        var memberPath = CommandArguments.RequireString(context.Arguments, 0, "member path");

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            object? value;

            try
            {
                value = context.Runtime.ObjectAccessor.GetValue(item, memberPath);
            }
            catch (Exception exception) when (exception is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Could not read member '{memberPath}': {exception.Message}");
            }

            yield return value;
        }
    }

    private static bool TryGetIndex(object? argument, out int index)
    {
        index = 0;

        if (argument is int i)
        {
            index = i;
            return i >= 0;
        }

        if (argument is long l && l >= 0 && l <= int.MaxValue)
        {
            index = (int)l;
            return true;
        }

        if (argument is double d && d >= 0 && d == Math.Floor(d) && d <= int.MaxValue)
        {
            index = (int)d;
            return true;
        }

        return false;
    }

    private static System.Dynamic.ExpandoObject Project(
        object? item,
        ProjectedMemberSelection selection,
        IObjectAccessor accessor)
    {
        return ShellRecordUtilities.CreateExpando(selection.MemberPaths
            .Select(memberPath => new KeyValuePair<string, object?>(
                NormalizeMemberPath(memberPath),
                accessor.GetValue(item, memberPath))));
    }

    private static string NormalizeMemberPath(string memberPath)
    {
        var path = MemberPath.Parse(memberPath);
        return string.Join(".", path.Segments.Select(segment => segment.Name));
    }

    private static async IAsyncEnumerable<object?> WrapTreeFlat(TreeEntryInfo root)
    {
        await Task.CompletedTask;

        foreach (var item in FlattenDisplayOrder(root))
        {
            yield return item;
        }
    }

    private static IEnumerable<TreeEntryInfo> FlattenDisplayOrder(TreeEntryInfo node)
    {
        yield return node with { Children = Array.Empty<TreeEntryInfo>() };

        foreach (var child in node.Children)
        {
            foreach (var descendant in FlattenDisplayOrder(child))
            {
                yield return descendant;
            }
        }
    }
}

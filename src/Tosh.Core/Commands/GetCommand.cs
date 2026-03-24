namespace Tosh.Core.Commands;

public sealed class GetCommand : ShellCommand
{
    public GetCommand(string name = "get")
        : base(name, "Projects one or more members from each pipeline object.", $"{name} <member-path> or {name} {{ <member-path>, ... }}") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("Missing required argument: member path.");
        }

        if (context.Arguments[0] is ProjectedMemberSelection selection)
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                yield return Project(item, selection, context.Runtime.ObjectAccessor);
            }

            yield break;
        }

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

    private static ProjectedObject Project(
        object? item,
        ProjectedMemberSelection selection,
        IObjectAccessor accessor)
    {
        var fields = selection.MemberPaths
            .Select(memberPath => new ProjectedField(
                NormalizeMemberPath(memberPath),
                memberPath,
                accessor.GetValue(item, memberPath)))
            .ToArray();

        return new ProjectedObject(fields);
    }

    private static string NormalizeMemberPath(string memberPath)
    {
        var path = MemberPath.Parse(memberPath);
        return string.Join(".", path.Segments.Select(segment => segment.Name));
    }
}

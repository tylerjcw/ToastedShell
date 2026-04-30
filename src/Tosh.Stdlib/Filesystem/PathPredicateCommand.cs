using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
public sealed class PathPredicateCommand : ShellCommand
{
    private readonly Func<string, bool> _predicate;
    private readonly string _fieldName;

    public PathPredicateCommand(string name, string description, string fieldName, Func<string, bool> predicate)
        : base(name, description, $"{name} <path> [path...]")
    {
        _fieldName = fieldName;
        _predicate = predicate;
    }

    public override CommandMetadata GetMetadata(IReadOnlyList<string>? aliases = null)
    {
        var metadata = base.GetMetadata(aliases);
        return metadata with
        {
            Arguments =
            [
                new CommandArgumentMetadata("path ...", "One or more filesystem paths to test. Paths may also be supplied from the pipeline.", true, "path-like", "path"),
            ],
            Examples =
            [
                new CommandExampleMetadata($"{Name} README.md", "Check a single path"),
                new CommandExampleMetadata($"glob \"src/**/*.cs\" | {Name} | where _.{_fieldName}", "Filter path-test results"),
            ],
            Output = $"Structured records with Path and {_fieldName} fields.",
            OutputType = "PathPredicateResult",
            OutputMembers = $"Path, {_fieldName}",
        };
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var paths = await ShellPathArguments.CollectAsync(context, context.Arguments, context.CancellationToken);

        foreach (var path in paths)
        {
            yield return ShellRecordUtilities.CreateExpando(
            [
                new KeyValuePair<string, object?>("Path", path),
                new KeyValuePair<string, object?>(_fieldName, _predicate(path)),
            ]);
        }
    }
}

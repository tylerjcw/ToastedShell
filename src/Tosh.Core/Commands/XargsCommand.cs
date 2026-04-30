namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("command", "Command to invoke for the generated arguments.")]
[CommandArgument("fixed-arg ...", "Arguments that are always passed before piped arguments.", Required = false)]
[CommandOption("-n, --max-args <count>", "Invoke the command repeatedly with at most this many piped arguments per invocation.")]
[CommandExample("echo README.md docs/INDEX.md | xargs wc -l", Title = "Pass piped words as command arguments")]
[CommandExample("glob \"*.log\" | xargs -n 1 rm", Title = "Run one invocation per input argument")]
[CommandOutput("Streams whatever the invoked sub-command produces, once per batch of input arguments.")]
public sealed class XargsCommand : ShellCommand
{
    public XargsCommand()
        : base("xargs", "Builds command invocations from text input.", "xargs [-n count] <command> [fixed-arg ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var evaluator = context.Runtime.Evaluator
                        ?? throw new InvalidOperationException("This runtime cannot evaluate nested commands for xargs.");
        var options = ParseOptions(context.Arguments);
        var inputValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var inputArguments = TokenizeInput(inputValues);
        var chunkSize = options.MaxArguments ?? int.MaxValue;

        if (inputArguments.Count == 0)
        {
            await foreach (var value in evaluator.EvaluateAsync(BuildInvocation(options.CommandAndArguments), "<xargs>", context.CancellationToken)
                               .WithCancellation(context.CancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        for (var offset = 0; offset < inputArguments.Count; offset += chunkSize)
        {
            var invocation = BuildInvocation(options.CommandAndArguments.Concat(inputArguments.Skip(offset).Take(chunkSize)));

            await foreach (var value in evaluator.EvaluateAsync(invocation, "<xargs>", context.CancellationToken)
                               .WithCancellation(context.CancellationToken))
            {
                yield return value;
            }
        }
    }

    private static IReadOnlyList<string> TokenizeInput(IReadOnlyList<object?> values)
    {
        return values
            .SelectMany(value => ExternalTextSerializer.Serialize(value)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
    }

    private static string BuildInvocation(IEnumerable<object?> arguments)
    {
        return string.Join(" ", arguments.Select(ShellCommandLineEscaper.Quote));
    }

    private static XargsOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        int? maxArguments = null;
        var commandAndArguments = new List<object?>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (commandAndArguments.Count == 0 && text is "-n" or "--max-args")
            {
                maxArguments = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                continue;
            }

            commandAndArguments.Add(arguments[index]);
        }

        if (commandAndArguments.Count == 0)
        {
            throw new InvalidOperationException("xargs requires a command to invoke.");
        }

        return new XargsOptions(maxArguments, commandAndArguments);
    }

    private sealed record XargsOptions(int? MaxArguments, IReadOnlyList<object?> CommandAndArguments);
}

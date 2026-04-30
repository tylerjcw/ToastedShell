using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("callable|block", "A lambda or block executed once per input item.")]
[CommandExample("echo one two | each { _.ToUpper() }", Title = "Transform each item to uppercase")]
[CommandExample("DriveInfo.GetDrives() | each func(d) => ($d.Name)", Title = "Extract drive names")]
[CommandNote("Collections stay intact until you explicitly expand them with `each` or `flatten`.")]
[CommandOutput("Returns whatever values the callable or block emits for each input item.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the current pipeline and executes the callable or block once per input item.")]
public sealed class EachCommand : ShellCommand
{
    public EachCommand(string name = "each")
        : base(name, "Executes a block or callable once for each input object.", $"{name} <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.each_requires_callable_or_block",
                title: "'each' requires exactly one callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);
        operation = await FunctionalCommandUtilities.ResolveCallableOrBlockAsync(context, operation);
        var executor = context.Runtime.BlockExecutor;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var iterationValues = new List<object?>();
            var shouldBreak = false;
            var shouldContinue = false;

            try
            {
                if (operation is ShellBlock block)
                {
                    if (executor is null)
                    {
                        throw new InvalidOperationException("Block execution is not available in this runtime.");
                    }

                    await foreach (var value in executor.ExecuteAsync(
                                       block,
                                       new Dictionary<string, object?>(StringComparer.Ordinal)
                                       {
                                           ["_"] = item,
                                       },
                                       context.CancellationToken)
                                       .WithCancellation(context.CancellationToken))
                    {
                        iterationValues.Add(value);
                    }
                }
                else
                {
                    var results = await FunctionalCommandUtilities.ExecuteAsync(
                        context,
                        operation,
                        [item],
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["_"] = item,
                        });

                    foreach (var value in results)
                    {
                        iterationValues.Add(value);
                    }
                }
            }
            catch (ContinueSignalException)
            {
                shouldContinue = true;
            }
            catch (BreakSignalException)
            {
                shouldBreak = true;
            }

            foreach (var value in iterationValues)
            {
                yield return value;
            }

            if (shouldBreak)
            {
                yield break;
            }

            if (shouldContinue)
            {
                continue;
            }
        }
    }
}

namespace Tosh.Core.Commands.Scripting;

[Stdlib(StdlibCategory.Scripting)]
[CommandCategory("Scripting")]
[CommandArgument("predicate", "Predicate callable or block that must evaluate truthy.")]
[CommandArgument("message", "Optional diagnostic message emitted when the assertion fails.", Required = false)]
[CommandExample("assert { (2 + 2) == 4 }", Title = "Assert an invariant")]
[CommandExample("assert { $env.HOME != null } \"HOME must be set\"", Title = "Assertion with a custom message")]
[CommandOutput("Emits nothing on success; throws a diagnostic when the predicate is falsy.")]
public sealed class AssertCommand : ShellCommand
{
    public AssertCommand()
        : base("assert", "Asserts that a condition is true; throws a diagnostic error if it is false.", "assert <predicate> [message]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.assert_requires_predicate",
                title: "The 'assert' command requires a predicate block or callable.",
                label: "pass a block like '{ $x > 0 }' or a callable");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);
        operation = await FunctionalCommandUtilities.ResolveCallableOrBlockAsync(context, operation);

        string? message = context.Arguments.Count >= 2
            ? context.Arguments[1]?.ToString()
            : null;

        var result = await FunctionalCommandUtilities.EvaluatePredicateAsync(
            context,
            operation,
            Array.Empty<object?>());

        if (!result)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.assertion_failed",
                title: message ?? "Assertion failed.",
                label: "this assertion evaluated to false");
        }

        // Pass through pipeline input unchanged when assertion succeeds
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            yield return item;
        }
    }
}

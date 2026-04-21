namespace Tosh.Core.Commands;

[CommandCategory("System")]
[CommandExample("whoami", Title = "Show the current user")]
public sealed class WhoAmICommand : ShellCommand
{
    public WhoAmICommand()
        : base("whoami", "Returns the current user principal.", "whoami") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            throw new InvalidOperationException("whoami does not accept arguments.");
        }

        yield return UnixSystemServices.GetCurrentIdentity().User;
    }
}

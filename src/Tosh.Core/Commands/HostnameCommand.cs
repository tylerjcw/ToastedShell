namespace Tosh.Core.Commands;

[CommandCategory("System")]
public sealed class HostnameCommand : ShellCommand
{
    public HostnameCommand()
        : base("hostname", "Returns the current host name.", "hostname") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            throw new InvalidOperationException("hostname does not accept arguments yet.");
        }

        yield return UnixSystemServices.GetHostName();
    }
}

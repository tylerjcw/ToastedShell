namespace Tosh.Core.Commands;

[CommandCategory("System")]
[CommandExample("uptime", Title = "Show uptime and load averages")]
public sealed class UptimeCommand : ShellCommand
{
    public UptimeCommand()
        : base("uptime", "Returns system uptime and load averages.", "uptime") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            throw new InvalidOperationException("uptime does not accept arguments.");
        }

        var info = SystemInfoServices.GetUptime()
                   ?? throw new InvalidOperationException("System uptime information is not available on this platform.");

        yield return info;
    }
}

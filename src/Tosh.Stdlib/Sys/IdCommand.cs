using Tosh.Runtime;

namespace Tosh.Stdlib.Sys;

[Stdlib(StdlibCategory.Sys)]
[CommandCategory("System")]
[CommandOption("-u", "Print only the effective user ID.")]
[CommandOption("-g", "Print only the effective group ID.")]
[CommandOption("-G", "Print all group IDs.")]
[CommandOption("-n", "Print names instead of numeric IDs (with -u, -g, or -G).")]
[CommandExample("id", Title = "Show full identity information")]
[CommandExample("id -un", Title = "Show the current username")]
[CommandOutput("An identity record with Uid, Gid, User, Group, and Groups properties, or a single value with flags.")]
public sealed class IdCommand : ShellCommand
{
    public IdCommand()
        : base("id", "Returns current user and group identity information.", "id [-u|-g|-G] [-n]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var identity = UnixSystemServices.GetCurrentIdentity();
        var options = ParseOptions(context.Arguments);

        if (options.ShowUser)
        {
            yield return options.ShowNames ? identity.User : identity.Uid;
            yield break;
        }

        if (options.ShowGroup)
        {
            yield return options.ShowNames ? identity.Group : identity.Gid;
            yield break;
        }

        if (options.ShowGroups)
        {
            foreach (var group in identity.Groups)
            {
                yield return options.ShowNames ? group : group.Id;
            }

            yield break;
        }

        yield return identity;
    }

    private static IdOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var options = new IdOptions();

        foreach (var argument in arguments)
        {
            var text = argument?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!text.StartsWith("-", StringComparison.Ordinal) || text == "-")
            {
                throw new InvalidOperationException("id does not accept positional arguments yet.");
            }

            foreach (var option in text[1..])
            {
                switch (option)
                {
                    case 'u':
                        options.ShowUser = true;
                        break;
                    case 'g':
                        options.ShowGroup = true;
                        break;
                    case 'G':
                        options.ShowGroups = true;
                        break;
                    case 'n':
                        options.ShowNames = true;
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported id option '-{option}'.");
                }
            }
        }

        var selectionCount = (options.ShowUser ? 1 : 0) + (options.ShowGroup ? 1 : 0) + (options.ShowGroups ? 1 : 0);

        if (selectionCount > 1)
        {
            throw new InvalidOperationException("id accepts only one of -u, -g, or -G at a time.");
        }

        if (options.ShowNames && selectionCount == 0)
        {
            throw new InvalidOperationException("id -n must be combined with -u, -g, or -G.");
        }

        return options;
    }

    private sealed class IdOptions
    {
        public bool ShowUser { get; set; }

        public bool ShowGroup { get; set; }

        public bool ShowGroups { get; set; }

        public bool ShowNames { get; set; }
    }
}

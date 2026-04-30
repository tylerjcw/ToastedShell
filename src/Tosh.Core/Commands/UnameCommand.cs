namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.System)]
[CommandCategory("System")]
[CommandOption("-s", "System Name")]
[CommandOption("-n", "Node Name")]
[CommandOption("-r", "Kernel Release")]
[CommandOption("-v", "Kernel Version")]
[CommandOption("-m", "Machine")]
[CommandOption("-o", "Operating System")]
[CommandOption("-a, --all", "Return the full structured uname object.")]
[CommandOption("--kernel-name", "Return the system/kernel name.")]
[CommandOption("--nodename", "Return the network node name.")]
[CommandOption("--kernel-release", "Return the kernel release.")]
[CommandOption("--kernel-version", "Return the kernel version.")]
[CommandOption("--machine", "Return the machine hardware name.")]
[CommandOption("--operating-system", "Return the operating system name.")]
[CommandExample("uname", Title = "Return structured system information")]
[CommandExample("uname -sr", Title = "Return selected fields")]
[CommandOutput("A record describing the host OS: kernel name, machine, version, and release.")]
public sealed class UnameCommand : ShellCommand
{
    public UnameCommand()
        : base("uname", "Returns kernel and operating system information as a Tosh object.", "uname [-a|-s|-n|-r|-v|-m|-o]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var info = UnixSystemServices.GetUname();
        var selectors = ParseSelectors(context.Arguments);

        if (selectors.Count == 0 || selectors.Contains('a'))
        {
            yield return info;
            yield break;
        }

        var fields = selectors
            .Select(selector => selector switch
            {
                's' => new KeyValuePair<string, object?>("SystemName", info.SystemName),
                'n' => new KeyValuePair<string, object?>("NodeName", info.NodeName),
                'r' => new KeyValuePair<string, object?>("Release", info.Release),
                'v' => new KeyValuePair<string, object?>("Version", info.Version),
                'm' => new KeyValuePair<string, object?>("Machine", info.Machine),
                'o' => new KeyValuePair<string, object?>("OperatingSystem", info.OperatingSystem),
                _ => throw new InvalidOperationException($"Unsupported uname selector '-{selector}'."),
            })
            .ToArray();

        if (fields.Length == 1)
        {
            yield return fields[0].Value;
            yield break;
        }

        yield return ShellRecordUtilities.CreateExpando(fields);
    }

    private static IReadOnlyList<char> ParseSelectors(IReadOnlyList<object?> arguments)
    {
        var selectors = new List<char>();

        foreach (var argument in arguments)
        {
            var text = argument?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                selectors.Add(text[2..] switch
                {
                    "all" => 'a',
                    "kernel-name" => 's',
                    "nodename" => 'n',
                    "kernel-release" => 'r',
                    "kernel-version" => 'v',
                    "machine" => 'm',
                    "operating-system" => 'o',
                    _ => throw new InvalidOperationException($"Unsupported uname option '{text}'."),
                });

                continue;
            }

            if (!text.StartsWith("-", StringComparison.Ordinal) || text.Length < 2)
            {
                throw new InvalidOperationException("uname does not accept positional arguments.");
            }

            foreach (var selector in text[1..])
            {
                selectors.Add(selector);
            }
        }

        return selectors;
    }
}

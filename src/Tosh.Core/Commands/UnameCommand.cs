namespace Tosh.Core.Commands;

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
                's' => new ProjectedField("SystemName", "SystemName", info.SystemName),
                'n' => new ProjectedField("NodeName", "NodeName", info.NodeName),
                'r' => new ProjectedField("Release", "Release", info.Release),
                'v' => new ProjectedField("Version", "Version", info.Version),
                'm' => new ProjectedField("Machine", "Machine", info.Machine),
                'o' => new ProjectedField("OperatingSystem", "OperatingSystem", info.OperatingSystem),
                _ => throw new InvalidOperationException($"Unsupported uname selector '-{selector}'."),
            })
            .ToArray();

        if (fields.Length == 1)
        {
            yield return fields[0].Value;
            yield break;
        }

        yield return new ProjectedObject(fields);
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

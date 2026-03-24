using System.Diagnostics;

namespace Tosh.Core.Commands;

public sealed class ProcessListCommand : ShellCommand
{
    public ProcessListCommand()
        : base("ps", "Lists running processes as Tosh process objects.", "ps [name-or-id ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var filters = parsed.Positionals
            .Select(argument => argument?.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        var processes = Process.GetProcesses()
            .OrderBy(process => SafeGetName(process), StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.Id);

        foreach (var process in processes)
        {
            using (process)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var info = ProcessInfo.From(process);

                if (filters.Length == 0 || MatchesAnyFilter(info, filters))
                {
                    yield return info;
                }
            }
        }
    }

    private static bool MatchesAnyFilter(ProcessInfo process, IReadOnlyList<string?> filters)
    {
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                continue;
            }

            if (string.Equals(process.Name, filter, StringComparison.OrdinalIgnoreCase) ||
                process.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(process.Id.ToString(), filter, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string SafeGetName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return process.Id.ToString();
        }
    }
}

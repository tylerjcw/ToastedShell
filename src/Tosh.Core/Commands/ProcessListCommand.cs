using System.Diagnostics;

namespace Tosh.Core.Commands;

public sealed class ProcessListCommand : ShellCommand
{
    public ProcessListCommand()
        : base("ps", "Lists running processes as Tosh process objects.", "ps [-eAf] [-p pid[,pid...]] [--ppid pid[,pid...]] [-u user[,user...]] [-t tty[,tty...]] [-o columns] [--sort field[,field...]] [--show columns] [--hide columns] [--show-all] [name-or-id ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var selection = CommandDisplaySelectionParser.Parse(context.Arguments, showOptionAliases: ["-o", "--output"]);
        var options = ParseOptions(selection.RemainingArguments);
        var filters = options.Positionals
            .Select(argument => argument?.ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        var effectiveSelection = GetEffectiveSelection(selection.Selection, options);

        var processes = Process
            .GetProcesses()
            .Select(ToProcessInfo)
            .Where(info => MatchesSelectors(info, options))
            .Where(info => filters.Length == 0 || MatchesAnyFilter(info, filters))
            .ToList();

        processes.Sort((left, right) => CompareProcesses(left, right, options.SortKeys));

        foreach (var process in processes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return CommandDisplaySelectionParser.Apply(context.Runtime, effectiveSelection, process);
        }
    }

    private static DisplayColumnSelection GetEffectiveSelection(DisplayColumnSelection selection, PsOptions options)
    {
        if (selection.HasOverrides || !options.FullPreset)
        {
            return selection;
        }

        return new DisplayColumnSelection(showColumns: ["Name", "Id", "Ppid", "User", "Tty", "Started", "Path"]);
    }

    private static ProcessInfo ToProcessInfo(Process process)
    {
        using (process)
        {
            return ProcessInfo.From(process);
        }
    }

    private static bool MatchesSelectors(ProcessInfo process, PsOptions options)
    {
        if (options.ProcessIds.Count > 0 && !options.ProcessIds.Contains(process.Id))
        {
            return false;
        }

        if (options.ParentIds.Count > 0 && (!process.ParentId.HasValue || !options.ParentIds.Contains(process.ParentId.Value)))
        {
            return false;
        }

        if (options.Users.Count > 0 && !MatchesUser(process, options.Users))
        {
            return false;
        }

        if (options.Ttys.Count > 0 && !MatchesTty(process, options.Ttys))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesUser(ProcessInfo process, IReadOnlySet<string> users)
    {
        var candidates = new[]
        {
            process.User?.Name,
            process.User?.DisplayName,
            process.User?.Id.ToString(),
            process.UserName,
        };

        return candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate) && users.Contains(candidate));
    }

    private static bool MatchesTty(ProcessInfo process, IReadOnlySet<string> ttys)
    {
        if (string.IsNullOrWhiteSpace(process.Tty))
        {
            return false;
        }

        if (ttys.Contains(process.Tty))
        {
            return true;
        }

        return process.Tty.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase) &&
               ttys.Contains(process.Tty["/dev/".Length..]);
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
                string.Equals(process.Id.ToString(), filter, StringComparison.Ordinal) ||
                (process.Path is not null && process.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static int CompareProcesses(ProcessInfo left, ProcessInfo right, IReadOnlyList<ProcessSortKey> sortKeys)
    {
        foreach (var sortKey in sortKeys)
        {
            var comparison = CompareProcessField(left, right, sortKey.Field);

            if (comparison == 0)
            {
                continue;
            }

            return sortKey.Descending ? -comparison : comparison;
        }

        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);

        if (nameComparison != 0)
        {
            return nameComparison;
        }

        return left.Id.CompareTo(right.Id);
    }

    private static int CompareProcessField(ProcessInfo left, ProcessInfo right, ProcessSortField field)
    {
        return field switch
        {
            ProcessSortField.Name => CompareNullableStrings(left.Name, right.Name),
            ProcessSortField.Id => left.Id.CompareTo(right.Id),
            ProcessSortField.ParentId => CompareNullableInts(left.ParentId, right.ParentId),
            ProcessSortField.Memory => CompareNullableLongs(left.Memory?.Bytes, right.Memory?.Bytes),
            ProcessSortField.Cpu => CompareNullableLongs(left.Cpu?.Ticks, right.Cpu?.Ticks),
            ProcessSortField.Started => CompareNullableDateTimes(left.Started, right.Started),
            ProcessSortField.User => CompareNullableStrings(left.User?.DisplayName, right.User?.DisplayName),
            ProcessSortField.Tty => CompareNullableStrings(left.Tty, right.Tty),
            ProcessSortField.Path => CompareNullableStrings(left.Path, right.Path),
            _ => 0,
        };
    }

    private static int CompareNullableStrings(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int CompareNullableInts(int? left, int? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return 0;
        }

        if (!left.HasValue)
        {
            return 1;
        }

        if (!right.HasValue)
        {
            return -1;
        }

        return left.Value.CompareTo(right.Value);
    }

    private static int CompareNullableLongs(long? left, long? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return 0;
        }

        if (!left.HasValue)
        {
            return 1;
        }

        if (!right.HasValue)
        {
            return -1;
        }

        return left.Value.CompareTo(right.Value);
    }

    private static int CompareNullableDateTimes(DateTime? left, DateTime? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return 0;
        }

        if (!left.HasValue)
        {
            return 1;
        }

        if (!right.HasValue)
        {
            return -1;
        }

        return left.Value.CompareTo(right.Value);
    }

    private static PsOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var options = new PsOptions();
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                options.Positionals.Add(argument);
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                ParseLongOption(text, arguments, ref index, options);
                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal) && text.Length > 1)
            {
                ParseShortOptions(text, arguments, ref index, options);
                continue;
            }

            options.Positionals.Add(argument);
        }

        if (options.SortKeys.Count == 0)
        {
            options.SortKeys.Add(new ProcessSortKey(ProcessSortField.Name, false));
            options.SortKeys.Add(new ProcessSortKey(ProcessSortField.Id, false));
        }

        return options;
    }

    private static void ParseLongOption(string text, IReadOnlyList<object?> arguments, ref int index, PsOptions options)
    {
        SplitLongOption(text, out var name, out var inlineValue);

        switch (name)
        {
            case "all":
                return;
            case "full":
                options.FullPreset = true;
                return;
            case "pid":
                AddProcessIds(options.ProcessIds, inlineValue ?? RequireOptionValue(arguments, ref index, "--pid"), "--pid");
                return;
            case "ppid":
                AddProcessIds(options.ParentIds, inlineValue ?? RequireOptionValue(arguments, ref index, "--ppid"), "--ppid");
                return;
            case "user":
                AddStrings(options.Users, inlineValue ?? RequireOptionValue(arguments, ref index, "--user"));
                return;
            case "tty":
                AddStrings(options.Ttys, inlineValue ?? RequireOptionValue(arguments, ref index, "--tty"));
                return;
            case "sort":
                AddSortKeys(options.SortKeys, inlineValue ?? RequireOptionValue(arguments, ref index, "--sort"));
                return;
            default:
                throw new InvalidOperationException($"Unknown option '{text}'.");
        }
    }

    private static void ParseShortOptions(string text, IReadOnlyList<object?> arguments, ref int index, PsOptions options)
    {
        for (var characterIndex = 1; characterIndex < text.Length; characterIndex++)
        {
            var option = text[characterIndex];

            switch (option)
            {
                case 'e':
                case 'A':
                    break;
                case 'f':
                    options.FullPreset = true;
                    break;
                case 'p':
                case 'u':
                case 'U':
                case 't':
                    var value = characterIndex + 1 < text.Length
                        ? text[(characterIndex + 1)..]
                        : RequireOptionValue(arguments, ref index, $"-{option}");

                    switch (option)
                    {
                        case 'p':
                            AddProcessIds(options.ProcessIds, value, "-p");
                            break;
                        case 'u':
                        case 'U':
                            AddStrings(options.Users, value);
                            break;
                        case 't':
                            AddStrings(options.Ttys, value);
                            break;
                    }

                    return;
                default:
                    throw new InvalidOperationException($"Unknown option '-{option}'.");
            }
        }
    }

    private static void SplitLongOption(string text, out string name, out string? value)
    {
        var separatorIndex = text.IndexOf('=', StringComparison.Ordinal);

        if (separatorIndex < 0)
        {
            name = text[2..];
            value = null;
            return;
        }

        name = text[2..separatorIndex];
        value = text[(separatorIndex + 1)..];
    }

    private static string RequireOptionValue(IReadOnlyList<object?> arguments, ref int index, string optionName)
    {
        index++;

        if (index >= arguments.Count || arguments[index]?.ToString() is not { Length: > 0 } text)
        {
            throw new InvalidOperationException($"Option '{optionName}' requires a value.");
        }

        return text;
    }

    private static void AddProcessIds(HashSet<int> target, string specification, string optionName)
    {
        foreach (var candidate in specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(candidate, out var value))
            {
                throw new InvalidOperationException($"Option '{optionName}' requires one or more integer process ids.");
            }

            target.Add(value);
        }
    }

    private static void AddStrings(HashSet<string> target, string specification)
    {
        foreach (var candidate in specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                target.Add(candidate);
            }
        }
    }

    private static void AddSortKeys(List<ProcessSortKey> target, string specification)
    {
        target.Clear();

        foreach (var candidate in specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var descending = candidate.StartsWith("-", StringComparison.Ordinal);
            var fieldText = candidate.TrimStart('+', '-');

            if (string.IsNullOrWhiteSpace(fieldText))
            {
                continue;
            }

            target.Add(new ProcessSortKey(ParseSortField(fieldText), descending));
        }

        if (target.Count == 0)
        {
            throw new InvalidOperationException("Option '--sort' requires one or more process fields.");
        }
    }

    private static ProcessSortField ParseSortField(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "name" or "comm" or "command" => ProcessSortField.Name,
            "id" or "pid" => ProcessSortField.Id,
            "ppid" or "parent" or "parentid" => ProcessSortField.ParentId,
            "memory" or "mem" or "rss" => ProcessSortField.Memory,
            "cpu" or "time" => ProcessSortField.Cpu,
            "started" or "start" or "starttime" => ProcessSortField.Started,
            "user" or "uid" => ProcessSortField.User,
            "tty" or "terminal" => ProcessSortField.Tty,
            "path" or "cmd" => ProcessSortField.Path,
            _ => throw new InvalidOperationException($"Unsupported ps sort field '{value}'."),
        };
    }

    private sealed class PsOptions
    {
        public List<object?> Positionals { get; } = [];

        public HashSet<int> ProcessIds { get; } = [];

        public HashSet<int> ParentIds { get; } = [];

        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Ttys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ProcessSortKey> SortKeys { get; } = [];

        public bool FullPreset { get; set; }
    }

    private readonly record struct ProcessSortKey(ProcessSortField Field, bool Descending);

    private enum ProcessSortField
    {
        Name,
        Id,
        ParentId,
        Memory,
        Cpu,
        Started,
        User,
        Tty,
        Path,
    }
}

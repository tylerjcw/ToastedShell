namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.System)]
[CommandCategory("System")]
[CommandArgument("name ...", "With no assignments, query one or more environment variable names.", Required = false)]
[CommandArgument("name=value ...", "Temporary environment assignments for `env` output or a nested command.", Required = false)]
[CommandArgument("command ...", "Optional nested command to run under the temporary environment snapshot.", Required = false)]
[CommandOption("-u <name>", "Unset a variable in the temporary environment snapshot.")]
[CommandOption("--", "Treat the remaining arguments as the nested command even when there are no assignments.")]
[CommandExample("env PATH", Title = "Inspect the structured PATH entry")]
[CommandExample("echo $env.PATH", Title = "Read just the PATH value")]
[CommandExample("echo $env.EDITOR", Title = "Read another environment variable directly")]
[CommandNote("Env keeps its object-returning query mode, but it can also build a temporary environment snapshot with `name=value` and `-u name`, optionally running a nested command under that snapshot.")]
[CommandOutput("Returns typed environment-variable entries, or the nested command's output when assignments/unsets are used with a command. Use `$env.NAME` for direct value-only access when you want the variable value instead of the structured `env` entry object. Missing `$env` members resolve to `null` rather than raising a missing-member error.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "With no explicit names, piped scalar values are treated as queried environment-variable names.")]
public sealed class EnvironmentCommand : ShellCommand
{
    public EnvironmentCommand()
        : base("env", "Lists, queries, sets, or unsets environment variables. Runs nested commands with temporary environment changes when a command follows.", "env [name ...] | env [-u name] [name=value ...] | env [-u name] [name=value ...] -- <command ...>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParseOptions(context.Arguments);

        if (parsed.HasMutationSemantics)
        {
            if (parsed.CommandArguments.Count == 0)
            {
                ApplyMutations(parsed);

                foreach (var name in parsed.UnsetNames)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return new EnvironmentVariableEntry(name, null, IsSet: false);
                }

                foreach (var pair in parsed.AssignedValues)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return new EnvironmentVariableEntry(pair.Key, pair.Value, IsSet: true);
                }

                yield break;
            }

            var evaluator = context.Runtime.Evaluator
                            ?? throw new InvalidOperationException("This runtime cannot evaluate nested commands for env.");

            var previousValues = CapturePreviousValues(parsed);

            try
            {
                ApplyMutations(parsed);

                var invocation = string.Join(" ", parsed.CommandArguments.Select(ShellCommandLineEscaper.Quote));

                await foreach (var value in evaluator.EvaluateAsync(invocation, "<env>", context.CancellationToken)
                                   .WithCancellation(context.CancellationToken))
                {
                    yield return value;
                }
            }
            finally
            {
                RestorePreviousValues(previousValues);
            }

            yield break;
        }

        IReadOnlyList<object?> names = parsed.QueryNames;

        if (names.Count == 0)
        {
            var pipedNames = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);

            if (pipedNames.Count == 0)
            {
                foreach (var entry in EnumerateEnvironment(Environment.GetEnvironmentVariables()))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                }

                yield break;
            }

            names = pipedNames.Cast<object?>().ToArray();
        }

        foreach (var argument in names)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var name = argument?.ToString();

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Environment variable names must be non-empty.");
            }

            var value = Environment.GetEnvironmentVariable(name);
            yield return new EnvironmentVariableEntry(name, value, value is not null);
        }
    }

    private static EnvironmentParseResult ParseOptions(IReadOnlyList<object?> arguments)
    {
        var assignments = new Dictionary<string, string?>(StringComparer.Ordinal);
        var unsets = new List<string>();
        var queryNames = new List<object?>();
        var commandArguments = new List<object?>();
        var parseOptions = true;
        var commandStarted = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || commandStarted || argument is not string text || text.Length == 0)
            {
                if (commandStarted)
                {
                    commandArguments.Add(argument);
                }
                else
                {
                    queryNames.Add(argument);
                }

                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                commandStarted = true;
                continue;
            }

            if (text is "-u" or "--unset")
            {
                if (++index >= arguments.Count)
                {
                    throw new InvalidOperationException("env option '-u' requires a variable name.");
                }

                var name = arguments[index]?.ToString();

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("Environment variable names must be non-empty.");
                }

                unsets.Add(name);
                continue;
            }

            if (text.StartsWith("--unset=", StringComparison.Ordinal))
            {
                var name = text["--unset=".Length..];

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("Environment variable names must be non-empty.");
                }

                unsets.Add(name);
                continue;
            }

            if (TryParseAssignment(text, out var assignmentName, out var assignmentValue))
            {
                assignments[assignmentName] = assignmentValue;
                continue;
            }

            if (assignments.Count > 0 || unsets.Count > 0)
            {
                commandStarted = true;
                commandArguments.Add(argument);
            }
            else
            {
                queryNames.Add(argument);
            }
        }

        return new EnvironmentParseResult(assignments, unsets, queryNames, commandArguments);
    }

    private static bool TryParseAssignment(string text, out string name, out string? value)
    {
        var separator = text.IndexOf('=');

        if (separator <= 0)
        {
            name = string.Empty;
            value = null;
            return false;
        }

        name = text[..separator];
        value = text[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(name);
    }

    private static IReadOnlyDictionary<string, string?> CapturePreviousValues(EnvironmentParseResult parsed)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var name in parsed.AssignedValues.Keys.Concat(parsed.UnsetNames).Distinct(StringComparer.Ordinal))
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
        }

        return previous;
    }

    private static void ApplyMutations(EnvironmentParseResult parsed)
    {
        foreach (var name in parsed.UnsetNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var pair in parsed.AssignedValues)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static void RestorePreviousValues(IReadOnlyDictionary<string, string?> previousValues)
    {
        foreach (var pair in previousValues)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static IReadOnlyList<EnvironmentVariableEntry> EnumerateEnvironment(System.Collections.IDictionary values)
    {
        return values
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => new EnvironmentVariableEntry(
                entry.Key?.ToString() ?? string.Empty,
                entry.Value?.ToString(),
                IsSet: true))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record EnvironmentParseResult(
        IReadOnlyDictionary<string, string?> AssignedValues,
        IReadOnlyList<string> UnsetNames,
        IReadOnlyList<object?> QueryNames,
        IReadOnlyList<object?> CommandArguments)
    {
        public bool HasMutationSemantics => AssignedValues.Count > 0 || UnsetNames.Count > 0 || CommandArguments.Count > 0;
    }
}

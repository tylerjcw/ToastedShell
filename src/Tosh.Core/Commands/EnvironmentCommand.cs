namespace Tosh.Core.Commands;

public sealed class EnvironmentCommand : ShellCommand
{
    public EnvironmentCommand()
        : base("env", "Lists environment variables as Tosh objects.", "env [name ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return new EnvironmentVariableEntry(
                    entry.Key?.ToString() ?? string.Empty,
                    entry.Value?.ToString(),
                    IsSet: true);
            }

            yield break;
        }

        foreach (var argument in parsed.Positionals)
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
}

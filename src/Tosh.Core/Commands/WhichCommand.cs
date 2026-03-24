namespace Tosh.Core.Commands;

public sealed class WhichCommand : ShellCommand
{
    public WhichCommand(string name = "which")
        : base(name, "Resolves built-in commands and external executables.", $"{name} <name ...>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            throw new InvalidOperationException($"The '{Name}' command requires at least one command name.");
        }

        foreach (var argument in parsed.Positionals)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var name = argument?.ToString();

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"The '{Name}' command requires non-empty command names.");
            }

            if (context.Runtime.Commands.TryGet(name, out var builtIn))
            {
                yield return new CommandResolution(
                    name,
                    builtIn is ICommandResolutionMetadata metadata
                        ? metadata.ResolutionKind
                        : CommandResolutionKind.BuiltIn,
                    Path: null,
                    builtIn.Description,
                    builtIn.Usage);
            }

            foreach (var path in ExternalCommandResolver.FindAllExecutables(context.Runtime.CurrentDirectory, name))
            {
                yield return new CommandResolution(
                    name,
                    CommandResolutionKind.External,
                    path,
                    Description: null,
                    Usage: null);
            }
        }
    }
}

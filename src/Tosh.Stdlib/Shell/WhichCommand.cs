using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[CommandCategory("Shell")]
[CommandArgument("name", "One or more command names to resolve.")]
[CommandExample("which ls", Title = "Find the ls command")]
[CommandExample("which git python node", Title = "Resolve multiple commands")]
[CommandOutput("Command resolution objects showing Kind (Builtin/External/Alias/Function), Name, and Path.", TypeName = "CommandResolution", Members = "Kind, Name, Path", ClrType = typeof(IAsyncEnumerable<CommandResolution>))]
public sealed class WhichCommand : ShellCommand
{
    public WhichCommand(string name = "which")
        : base(name, "Resolves built-in commands and external executables.", $"{name} <name ...>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        IReadOnlyList<object?> names = parsed.Positionals;

        if (names.Count == 0)
        {
            var pipedNames = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);

            if (pipedNames.Count == 0)
            {
                throw new InvalidOperationException($"The '{Name}' command requires at least one command name.");
            }

            names = pipedNames.Cast<object?>().ToArray();
        }

        foreach (var argument in names)
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

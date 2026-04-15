using System.Reflection;

namespace Tosh.Core;

public abstract class ShellCommand : IShellCommand
{
    protected ShellCommand(string name, string description, string usage)
    {
        Name = name;
        Description = description;
        Usage = usage;
    }

    public string Name { get; }

    public string Description { get; }

    public string Usage { get; }

    public abstract IAsyncEnumerable<object?> ExecuteAsync(CommandContext context);

    /// <summary>
    /// Builds canonical metadata for this command from its constructor values
    /// and attribute annotations. Subclasses may override to supply custom metadata.
    /// </summary>
    public virtual CommandMetadata GetMetadata(IReadOnlyList<string>? aliases = null)
    {
        var type = GetType();

        var category = type.GetCustomAttribute<CommandCategoryAttribute>()?.Category
                       ?? "Shell";

        var arguments = type.GetCustomAttributes<CommandArgumentAttribute>()
            .Select(a => new CommandArgumentMetadata(a.Name, a.Description, a.Required, a.TypeName))
            .ToList();

        var options = type.GetCustomAttributes<CommandOptionAttribute>()
            .Select(o => new CommandOptionMetadata(o.Syntax, o.Description))
            .ToList();

        var examples = type.GetCustomAttributes<CommandExampleAttribute>()
            .Select(e => new CommandExampleMetadata(e.Code, e.Title))
            .ToList();

        var notes = type.GetCustomAttributes<CommandNoteAttribute>()
            .Select(n => n.Text)
            .ToList();

        var output = type.GetCustomAttribute<CommandOutputAttribute>()?.Description;

        CommandPipelineInputMetadata? pipelineInput = null;
        var pipeAttr = type.GetCustomAttribute<PipelineInputAttribute>();
        if (pipeAttr is not null)
            pipelineInput = new(pipeAttr.AcceptsScalar, pipeAttr.AcceptsRecord, pipeAttr.AcceptsList, pipeAttr.AcceptsTable, pipeAttr.Description);

        return new CommandMetadata(
            Name: Name,
            Description: Description,
            Usage: Usage,
            Category: category,
            Aliases: aliases ?? [],
            Arguments: arguments,
            Options: options,
            Examples: examples,
            Notes: notes,
            Output: output,
            PipelineInput: pipelineInput);
    }
}

namespace Tosh.Core;

/// <summary>
/// Declares the command's category for help grouping and spec generation.
/// When absent, the category defaults to "Shell".
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandCategoryAttribute(string category) : Attribute
{
    public string Category { get; } = category;
}

/// <summary>
/// Declares that this command is an alias of another canonical command.
/// The exporter groups aliases together under the primary command's metadata entry.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandAliasAttribute(string canonicalName) : Attribute
{
    public string CanonicalName { get; } = canonicalName;
}

/// <summary>
/// Declares a positional argument accepted by the command.
/// Multiple attributes are allowed and define ordered parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandArgumentAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool Required { get; init; } = true;
    public string? TypeName { get; init; }
}

/// <summary>
/// Declares a flag or option accepted by the command.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandOptionAttribute(string syntax, string description) : Attribute
{
    public string Syntax { get; } = syntax;
    public string Description { get; } = description;
}

/// <summary>
/// Declares an example invocation of the command.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandExampleAttribute(string code) : Attribute
{
    public string Code { get; } = code;
    public string? Title { get; init; }
}

/// <summary>
/// Adds a note or extended remark to the command's documentation.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandNoteAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// <summary>
/// Describes what the command produces on its output pipeline.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandOutputAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

/// <summary>
/// Describes what the command accepts from its input pipeline.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PipelineInputAttribute : Attribute
{
    public bool AcceptsScalar { get; init; }
    public bool AcceptsRecord { get; init; }
    public bool AcceptsList { get; init; }
    public bool AcceptsTable { get; init; }
    public string? Description { get; init; }
}

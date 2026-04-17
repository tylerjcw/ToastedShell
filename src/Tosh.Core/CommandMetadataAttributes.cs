namespace Tosh.Core;
/// <summary>
/// Adds a long description or extended help text to the command.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandLongDescriptionAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// <summary>
/// Declares the version in which the command was introduced.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandSinceAttribute(string version) : Attribute
{
    public string Version { get; } = version;
}

/// <summary>
/// Declares the version in which the command was deprecated.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandDeprecatedAttribute(string version) : Attribute
{
    public string Version { get; } = version;
}

/// <summary>
/// Declares the version in which the command was removed.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandRemovedAttribute(string version) : Attribute
{
    public string Version { get; } = version;
}

/// <summary>
/// Adds a tag or keyword for discoverability/search.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandTagAttribute(string tag) : Attribute
{
    public string Tag { get; } = tag;
}

/// <summary>
/// Declares a related command for see-also/help navigation.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandSeeAlsoAttribute(string relatedCommand) : Attribute
{
    public string RelatedCommand { get; } = relatedCommand;
}

/// <summary>
/// Declares a required permission or capability.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Marks a command as experimental.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandExperimentalAttribute : Attribute { }

/// <summary>
/// Declares a possible error condition or exit code.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandErrorConditionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

/// <summary>
/// Declares a canonical example with input and expected output.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandCanonicalExampleAttribute(string input, string output, string? description = null) : Attribute
{
    public string Input { get; } = input;
    public string Output { get; } = output;
    public string? Description { get; } = description;
}

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

    /// <summary>
    /// The syntactic kind: "expression", "bareword", "string", "path", "block", or "any".
    /// When null, the exporter infers the kind from <see cref="TypeName"/>.
    /// </summary>
    public string? Kind { get; init; }
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

    /// <summary>CLR type name(s) of the output objects, e.g. "FileSystemEntry" or "String".</summary>
    public string? TypeName { get; init; }

    /// <summary>Comma-separated key member names, e.g. "Name, Size, Modified".</summary>
    public string? Members { get; init; }

    /// <summary>"structured" (typed objects), "text" (plain text lines), "mixed", or "none".</summary>
    public string Mode { get; init; } = "structured";
}

/// <summary>
/// Declares the side effects a command may perform.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandSideEffectsAttribute : Attribute
{
    public bool ReadsFiles { get; init; }
    public bool WritesFiles { get; init; }
    public bool Network { get; init; }
    public bool SpawnsProcess { get; init; }
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

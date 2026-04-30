namespace Tosh.Runtime;

/// <summary>
/// Coarse, compile-time-relevant classification of a command for the future
/// "compiled ToastScript" partition described in <c>docs/COMPILED_TOSH.md</c>.
///
/// The buckets here parallel the future <c>Tosh.Stdlib.*</c> assembly split:
/// each value names the standard-library category a command would live in
/// once <c>Tosh.Runtime</c> is broken apart. Today the values are pure metadata —
/// they drive <see cref="StdlibAttribute"/> tagging and have no runtime effect.
///
/// This enum is orthogonal to the free-form <see cref="CommandCategoryAttribute"/>
/// label used for help-system grouping. <c>CommandCategory</c> is end-user-facing
/// ("Filesystem", "Pipeline", …) and may evolve; <see cref="StdlibCategory"/>
/// names the future assembly boundary and is intended to be more stable.
/// </summary>
public enum StdlibCategory
{
    /// <summary>Filesystem operations: <c>ls</c>, <c>cd</c>, <c>cp</c>, <c>find</c>, …</summary>
    Filesystem,
    /// <summary>File and stream I/O: <c>read-file</c>, <c>write-file</c>, <c>open-file</c>, …</summary>
    IO,
    /// <summary>Process management: <c>ps</c>, <c>kill</c>, <c>spawn</c>, <c>jobs</c>, …</summary>
    Processes,
    /// <summary>System inspection: <c>uname</c>, <c>df</c>, <c>systemctl</c>, …</summary>
    Sys,
    /// <summary>Text manipulation: <c>grep</c>, <c>cut</c>, <c>tr</c>, <c>parse</c>, …</summary>
    Text,
    /// <summary>Data / collection helpers: <c>collect</c>, <c>distinct</c>, <c>group-by</c>, <c>summarize</c>, …</summary>
    Data,
    /// <summary>Pipeline-stage combinators: <c>where</c>, <c>first</c>, <c>partition</c>, <c>flat-map</c>, …</summary>
    Pipeline,
    /// <summary>Functional / iterator generators: <c>map</c>, <c>reduce</c>, <c>scan</c>, <c>repeat</c>, <c>compose</c>, …</summary>
    Functional,
    /// <summary>Concurrency primitives: <c>async</c>, <c>parallel</c>, <c>channel</c>, …</summary>
    Concurrency,
    /// <summary>Network operations: <c>http</c>, <c>ping</c>, <c>ip</c>, …</summary>
    Net,
    /// <summary>Time and date: <c>date</c>, <c>time</c>, <c>sleep</c>, <c>timespan</c>.</summary>
    Time,
    /// <summary>Cryptography: <c>hash</c>, <c>guid</c>.</summary>
    Crypto,
    /// <summary>Output presentation: <c>styled</c>, <c>view</c> (output-tuning subset).</summary>
    Display,
    /// <summary>Math helpers: <c>abs</c>, <c>round</c>, <c>min</c>, <c>max</c>, …</summary>
    Maths,
    /// <summary>CLR interop: <c>new-object</c>, <c>call</c>, <c>cast</c>, <c>members</c>, …</summary>
    Clr,
    /// <summary>Scripting / runtime meta: <c>source</c>, <c>eval</c>, <c>assert</c>, <c>echo</c>, …</summary>
    Scripting,
    /// <summary>Shell-host facilities (REPL state, history, prompt). See <see cref="ShellOnlyAttribute"/>.</summary>
    Shell,
}

/// <summary>
/// Marks a command's standard-library bucket — i.e. the future <c>Tosh.Stdlib.*</c>
/// assembly it would live in once the language and shell are split apart
/// (<c>docs/COMPILED_TOSH.md</c>).
///
/// This attribute is additive and orthogonal to <see cref="CommandCategoryAttribute"/>:
/// <c>CommandCategory</c> remains the user-facing help-grouping label;
/// <c>Stdlib</c> is the compile-time, binding-relevant classification.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class StdlibAttribute(StdlibCategory category) : Attribute
{
    public StdlibCategory Category { get; } = category;
}

/// <summary>
/// Marks a command as depending on interactive-shell state (REPL history,
/// directory stack, prompt rendering, TUI, etc.) and therefore unsuitable
/// for use outside an interactive session.
///
/// At runtime, executing a <see cref="ShellOnlyAttribute"/>-marked command
/// from a non-interactive script (<c>tosh script.tosh</c> or <c>tosh -c …</c>)
/// emits a warning with code <c>tosh.shell_only</c>. The warning is hushable
/// via <c># hush tosh.shell_only</c>, scope-level hush, or
/// <c>$tosh.Config.Diagnostics.Hushed</c>.
///
/// In a future compiled-ToastScript build, calling a <c>[ShellOnly]</c>
/// command will be a compile-time error (see <c>docs/COMPILED_TOSH.md</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ShellOnlyAttribute(string? reason = null) : Attribute
{
    /// <summary>Optional human-readable explanation surfaced in the warning.</summary>
    public string? Reason { get; } = reason;
}



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

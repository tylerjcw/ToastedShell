using System.Reflection;

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
/// Resolves a command type's <see cref="StdlibCategory"/> by checking, in order:
///   1. an explicit <see cref="StdlibAttribute"/> on the type (override);
///   2. the type's namespace — if it starts with <c>Tosh.Stdlib.</c> followed
///      by an <see cref="StdlibCategory"/> name, that name is returned.
///
/// Returns <c>null</c> when neither rule applies. The namespace fallback lets
/// commands placed under <c>src/Tosh.Stdlib/Filesystem/</c>, <c>…/Text/</c>,
/// etc. omit the attribute — the folder is already the source of truth.
/// </summary>
public static class StdlibCategoryResolver
{
    private const string StdlibNamespacePrefix = "Tosh.Stdlib.";

    public static StdlibCategory? Resolve(Type type)
    {
        var explicitAttr = type.GetCustomAttribute<StdlibAttribute>();
        if (explicitAttr is not null)
        {
            return explicitAttr.Category;
        }

        var ns = type.Namespace;
        if (ns is null || !ns.StartsWith(StdlibNamespacePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var leaf = ns.AsSpan(StdlibNamespacePrefix.Length);
        var nextDot = leaf.IndexOf('.');
        if (nextDot >= 0)
        {
            leaf = leaf[..nextDot];
        }

        return Enum.TryParse<StdlibCategory>(leaf.ToString(), ignoreCase: false, out var parsed)
            ? parsed
            : null;
    }
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
/// Streaming/throughput contract for a pipeline command. Surfaced in help
/// topics and metadata exports so users and the compiler can reason about
/// which builtins materialise the whole input vs. flow row-by-row.
/// </summary>
public enum StreamingBehavior
{
    /// <summary>
    /// Yields output as input arrives. Safe to compose on infinite streams.
    /// Examples: where, map, filter, each, flatmap, skip.
    /// </summary>
    Lazy,

    /// <summary>
    /// Lazy AND stops reading input once its result is satisfied. Composable
    /// with infinite streams; the upstream producer is cancelled as soon as
    /// the short-circuit fires. Examples: first, take-while, take-until,
    /// find-index, quantifier (any/all).
    /// </summary>
    ShortCircuit,

    /// <summary>
    /// Drains the entire input before yielding any output. Not safe on
    /// unbounded streams. Examples: sort, reverse, group-by, summarize,
    /// last, count, sum, distinct.
    /// </summary>
    Eager,
}

/// <summary>
/// Declares the command's streaming behaviour. Used by help, by the
/// streaming display sink, and by the future compiler/binder to reject
/// eager commands on lazy-only pipelines.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommandStreamingAttribute(StreamingBehavior behavior) : Attribute
{
    public StreamingBehavior Behavior { get; } = behavior;
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

    /// <summary>
    /// Optional CLR type the argument's value binds to. When set,
    /// the exporter populates <c>CommandArgumentMetadata.TypeInfo</c>
    /// with a structured <see cref="TypedTypeRef"/> derived from
    /// <see cref="TypedTypeRefBuilder.FromType"/>. Coexists with
    /// the free-form <see cref="TypeName"/> string.
    /// </summary>
    public Type? ClrType { get; init; }

    /// <summary>
    /// Human-readable refinement label preserved into the typed
    /// metadata (e.g. <c>"positive int"</c>, <c>"non-empty string"</c>).
    /// </summary>
    public string? Refinement { get; init; }

    /// <summary>
    /// True when this argument accepts zero or more values (variadic /
    /// rest parameter). The binder treats commands with any variadic
    /// argument as having an unbounded maximum arity.
    /// </summary>
    public bool Variadic { get; init; }

    /// <summary>
    /// True when this argument and every subsequent token should be
    /// treated as opaque positional values (option parsing stops once
    /// this argument starts being filled). Used for commands such as
    /// <c>spawn</c>/<c>exec</c> that forward arguments to an external
    /// process verbatim.
    /// </summary>
    public bool Passthrough { get; init; }
}

/// <summary>
/// Declares a flag or option accepted by the command.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandOptionAttribute(string syntax, string description) : Attribute
{
    public string Syntax { get; } = syntax;
    public string Description { get; } = description;

    /// <summary>
    /// Optional CLR type the option's value binds to (omit for
    /// boolean flags). Surfaces a structured
    /// <see cref="TypedTypeRef"/> on
    /// <c>CommandOptionMetadata.ValueTypeInfo</c>.
    /// </summary>
    public Type? ValueClrType { get; init; }

    /// <summary>True when the option is a value-less flag.</summary>
    public bool IsFlag { get; init; }

    /// <summary>
    /// Optional default value, displayed in the help-topic Options table
    /// and surfaced on <c>CommandOptionMetadata.Default</c>. Use the literal
    /// string the user would type (e.g. <c>"name"</c>, <c>"true"</c>,
    /// <c>"0"</c>); leave <c>null</c> when there is no meaningful default.
    /// </summary>
    public string? Default { get; init; }
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

    /// <summary>
    /// Optional CLR type the command yields. When set, the exporter
    /// populates <c>CommandMetadata.OutputTypeInfo</c> with a
    /// structured <see cref="TypedTypeRef"/>. The element-type
    /// inference handles <c>IAsyncEnumerable&lt;T&gt;</c> and
    /// <c>IEnumerable&lt;T&gt;</c> correctly.
    /// </summary>
    public Type? ClrType { get; init; }
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

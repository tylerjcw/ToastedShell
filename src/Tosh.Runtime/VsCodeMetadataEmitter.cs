using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tosh.Runtime;

/// <summary>
/// Generates VS Code extension language-data.json from canonical command metadata.
/// Keywords and special variables remain manually maintained here until they
/// get their own formal metadata model.
/// </summary>
public static class VsCodeMetadataEmitter
{
    public static string Emit(IReadOnlyList<CommandMetadata> metadata)
    {
        var builtins = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in metadata)
        {
            // Append the stdlib bucket and shell-only badge to each description so
            // VS Code hovers and completion details surface the same library /
            // shell-only partition documented in docs/COMPILED_TOSH.md.
            var description = entry.Description;
            var suffix = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(entry.Stdlib))
                suffix.Append($" *(`Tosh.Stdlib.{entry.Stdlib}`)*");
            if (entry.IsShellOnly)
                suffix.Append(" **[shell-only]**");
            if (suffix.Length > 0)
                description = description + suffix.ToString();

            builtins[entry.Name] = description;

            foreach (var alias in entry.Aliases)
                builtins[alias] = $"Alias for `{entry.Name}`.";
        }

        var languageData = new VsCodeLanguageData(
            Keywords: Keywords,
            SpecialVariables: SpecialVariables,
            Builtins: builtins);

        var options = new JsonSerializerOptions(VsCodeLanguageDataContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        return JsonSerializer.Serialize(languageData, options);
    }

    // ── Static keyword definitions ────────────────────────────────────

    private static readonly SortedDictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["alloc"] = "Allocate a native buffer into a variable by byte count or interop type size.",
        ["and"] = "Logical conjunction.",
        ["as"] = "Rename a CLR alias or required export in the current scope.",
        ["bind"] = "Attach native function bindings to a required native module.",
        ["break"] = "Exit the current loop.",
        ["callconv"] = "Override the native calling convention for a bound export.",
        ["case"] = "A `switch` match arm.",
        ["catch"] = "Handle a thrown value or runtime failure.",
        ["class"] = "Define a ToSh class with properties, constructors, and methods.",
        ["continue"] = "Skip the rest of the current loop iteration.",
        ["default"] = "Fallback arm for `switch`.",
        ["else"] = "Fallback branch for `if`.",
        ["enum"] = "Define a named enum with symbolic members and optional underlying numeric storage.",
        ["export"] = "Export a module declaration or environment variable.",
        ["finally"] = "Always run cleanup code after `try` / `catch`.",
        ["for"] = "Iterate over pipeline output or another enumerable source.",
        ["from"] = "Select a source file or module in selective `require` forms.",
        ["func"] = "Define a function. Use `func name(args) { ... }` or `func name => command`.",
        ["global"] = "Publish a declaration to the session-wide scope.",
        ["if"] = "Run a block when a condition evaluates to true.",
        ["in"] = "Membership operator and `for` loop keyword.",
        ["is"] = "Type or equality-style operator form.",
        ["is-not"] = "Negated `is` operator.",
        ["match"] = "Use `match (<value>) { pattern => result; default => fallback }` for expression-style branching, or `match <regex> ...` as the regex command.",
        ["module"] = "Define a named ToSh module object with its own lexical scope.",
        ["name-of"] = "Command-style alias for `nameof`.",
        ["nameof"] = "Return the name of a variable or member path.",
        ["native"] = "In `require native`, load a native shared library for binding and invocation.",
        ["new"] = "Construct a CLR object, ToSh named type, or shell collection type like `new list<String>(...)`.",
        ["not"] = "Logical negation.",
        ["not-in"] = "Negated membership operator.",
        ["or"] = "Logical disjunction.",
        ["out"] = "Mark a native binding parameter as output-only and return its updated value after the call.",
        ["prop"] = "Declare a class property, getter, setter, or computed member.",
        ["record"] = "Define a named data shape with positional construction, defaults, and structural equality.",
        ["ref"] = "Mark a native binding parameter as by-reference so the call can read and update it.",
        ["require"] = "Load a `.tosh` module, `.dll`, or `.csproj` once per session and import names lexically.",
        ["return"] = "Exit the current function or script early.",
        ["shy"] = "Keep a declaration in the current lexical scope, or hide a class member from public inspection and external access.",
        ["static"] = "Mark a class member as belonging to the class instead of an instance.",
        ["shared"] = "Mark a class member as belonging to the class instead of an instance (alias for static).",
        ["sealed"] = "Prevent a class from being inherited.",
        ["hollow"] = "Mark a class or method as abstract, requiring subclass implementation.",
        ["fixed"] = "Mark a class property as read-only after initialization.",
        ["vital"] = "Mark a class property as required during construction.",
        ["guarded"] = "Restrict member access to the defining class and its subclasses (protected).",
        ["overrule"] = "Override an inherited method from a parent class.",
        ["hermit"] = "Mark a class as static-only; all members are auto-promoted to shared.",
        ["strict"] = "Make all properties in a class read-only (immutable).",
        ["lazy"] = "Defer property initialization until first access.",
        ["fading"] = "Mark a member as deprecated; emits a warning on use.",
        ["local"] = "Restrict member visibility to the defining assembly (internal).",
        ["raw"] = "Mark a method for unsafe/native interop.",
        ["partial"] = "Allow a class definition to be split across multiple declarations.",
        ["proud"] = "Explicitly mark a member as public.",
        ["public"] = "Explicitly mark a member as public (no-op, members are public by default).",
        ["fluid"] = "Mark a struct as mutable, allowing field reassignment after construction.",
        ["struct"] = "Define a value-type with positional fields, structural equality, and copy-on-assign semantics.",
        ["trait"] = "Define a trait with required and default method/property signatures that classes can adopt via 'uses'.",
        ["fulfills"] = "Declare that a class conforms to one or more interfaces.",
        ["uses"] = "Declare that a class adopts one or more traits.",
        ["switch"] = "Legacy statement-style value matching with `case` blocks.",
        ["throw"] = "Raise an error value.",
        ["try"] = "Begin a `try` / `catch` / `finally` block.",
        ["until"] = "Repeat a block until a condition becomes true.",
        ["using"] = "Import CLR namespaces or create CLR aliases in the current lexical scope.",
        ["var"] = "Declare a variable. Reference it later as `$name`.",
        ["while"] = "Repeat a block while a condition remains true.",
        // Previously these lived only in the generated language-data.json, which
        // had been hand-edited; regenerating silently dropped them. They belong
        // here, in the source of truth, so the emitter round-trips losslessly.
        ["const"] = "Declare a constant value. Reference it later as `$name`. Cannot be reassigned.",
        ["contains"] = "Membership operator: returns true when the right operand is contained in the left.",
        ["defer"] = "Schedule a block to run when the enclosing scope exits, even on exception or early return.",
        ["ends-with"] = "String predicate: returns true when the left value ends with the right value.",
        ["event"] = "Declare an event that handlers can subscribe to via `handles`.",
        ["extends"] = "Declare that a class derives from a base class.",
        ["handles"] = "Register a handler block for a named event.",
        ["implements"] = "Synonym for `fulfills`: declare that a class implements one or more interfaces.",
        ["interface"] = "Define an interface: a set of method/property signatures classes can fulfill.",
        ["is-in"] = "Membership operator: returns true when the left value is in the right collection.",
        ["is-not-in"] = "Negated membership operator.",
        ["leaky"] = "Mark a struct as having reference-like (leaked) semantics.",
        ["let"] = "Bind a name in a pattern-match arm or comprehension.",
        ["once"] = "Limit an event handler so it fires at most one time.",
        ["pick"] = "Alias for `get`: project fields or pick a member by name.",
        ["priority"] = "Set the dispatch priority for an event handler.",
        ["private"] = "Synonym for `shy`: hide a class member from outside the class.",
        ["protected"] = "Synonym for `guarded`: restrict access to the defining class and its subclasses.",
        ["quote"] = "Capture an expression as an unevaluated quoted form for macro/rune use.",
        ["readonly"] = "Synonym for `fixed`: mark a class property as read-only after initialization.",
        ["rune"] = "Define a macro-like expansion that runs at parse time.",
        ["starts-with"] = "String predicate: returns true when the left value starts with the right value.",
        ["union"] = "Define a tagged union (sum type) of named variant cases.",
        ["unless"] = "Inverse of `if`: run a block when a condition is falsy. Also valid as a postfix guard on jump statements.",
        ["when"] = "Pattern guard or event-handler condition.",
        ["where"] = "Generic-constraint clause (`where T: Numeric`) or pipeline filter command.",
        ["yield"] = "Emit a value from a generator function or block.",
        // Refinement types
        ["type"] = "Define a refinement type: an existing type narrowed by `where` predicates, with optional `coerce` repair. Example: `type Port = int where (_ >= 1 and _ <= 65535)`.",
        ["coerce"] = "Repair clause in a refinement type. `if <guard> coerce <expr>` normalises before validation; a bare `coerce <expr>` fires only after a `where` predicate has failed.",
        // Property accessors
        ["get"] = "Getter body inside a `prop` declaration. Also the pipeline command that projects members, aliased as `pick` and `select`.",
        ["set"] = "Setter body inside a `prop` declaration. The incoming value is bound to `$value`.",
        // Subcommand dispatch
        ["subcommand"] = "Declare a named subcommand, turning a script into a structured CLI. Body runs when the user selects it; nest for command trees.",
        ["flag"] = "Declare an optional named option inside a `subcommand` body. Invoked as `--name value` or `--name=value`; booleans also accept `--no-name`.",
        ["arg"] = "Declare a required positional argument inside a `subcommand` body. Consumed in declaration order.",
        ["eager"] = "Subcommand modifier: run this body even when dispatch descends into a child. Used for setup work. Cannot combine with `hollow`.",
        ["hidden"] = "Subcommand modifier: exclude from auto-generated help and \"did you mean\" suggestions. Still fully callable.",
        // Modifiers that were missing from the editor's keyword table
        ["abstract"] = "Synonym for `hollow`: mark a class or method as abstract, requiring a subclass implementation.",
        ["obsolete"] = "Synonym for `fading`: mark a member as deprecated; emits a warning on use.",
        ["override"] = "Synonym for `overrule`: override an inherited method from a parent class.",
        ["required"] = "On an `event`, callers must handle it; on a property, a synonym for `vital`.",
    };

    // ── Static special-variable definitions ───────────────────────────

    private static readonly SortedDictionary<string, string> SpecialVariables = new(StringComparer.Ordinal)
    {
        ["$tosh"] = "The live ToSh runtime namespace root for config, script state, shell/session state, and host metadata.",
        ["_"] = "The current pipeline item in `where`, `each`, and similar contexts.",
        ["$this"] = "Self-reference inside class and struct method bodies.",
        ["$env"] = "Environment variables by name: `$env.HOME`. Lookup is case-insensitive, and assigning routes through the same export path as `export NAME = \"value\"`.",
        ["$value"] = "The incoming value inside a `prop` setter body.",
        ["$tosh.Last.Result"] = "Pipeline output of the most recent statement.",
        ["$tosh.Last.ExitCode"] = "Exit code of the last external process (`int`).",
        ["$tosh.Last.Duration"] = "Wall time of the last command (`TimeSpan`).",
        ["$tosh.Script.Path"] = "Absolute path of the running script file.",
        ["$tosh.Script.Name"] = "Filename portion of the running script.",
        ["$tosh.Script.Directory"] = "Directory containing the running script.",
        ["$tosh.Script.Args"] = "Arguments passed to the script (`list`).",
        ["$tosh.Function.Name"] = "Name of the currently executing function.",
        ["$tosh.Function.Args"] = "Arguments passed to the current function (`list`).",
        ["$tosh.Function.Input"] = "Pipeline input to the current function.",
        ["$tosh.Session.CurrentDirectory"] = "Shell working directory (equivalent to `pwd`).",
        ["$tosh.Session.JobCount"] = "Number of running background jobs.",
        ["$tosh.Session.OpenHandleCount"] = "Number of open file/stream handles.",
        ["$tosh.Session.NextHistoryId"] = "ID that will be assigned to the next history entry.",
        ["$tosh.IsLoginShell"] = "`true` when running as a login shell.",
        ["$tosh.Host.Version"] = "TōSh version string.",
        ["$tosh.Host.RuntimeId"] = "`.NET` runtime identifier (e.g. `linux-x64`).",
        ["$tosh.Host.Framework"] = "Target framework moniker (e.g. `net10.0`).",
        ["$tosh.Host.ProcessId"] = "PID of the current shell process.",
        ["$tosh.Config"] = "Live configuration object.",
    };
}

// ── Serialization model ───────────────────────────────────────────────

internal sealed record VsCodeLanguageData(
    [property: JsonPropertyName("keywords")]
    SortedDictionary<string, string> Keywords,
    [property: JsonPropertyName("specialVariables")]
    SortedDictionary<string, string> SpecialVariables,
    [property: JsonPropertyName("builtins")]
    SortedDictionary<string, string> Builtins);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VsCodeLanguageData))]
internal partial class VsCodeLanguageDataContext : JsonSerializerContext;

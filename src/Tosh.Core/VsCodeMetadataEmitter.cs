using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tosh.Core;

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
            builtins[entry.Name] = entry.Description;

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
    };

    // ── Static special-variable definitions ───────────────────────────

    private static readonly SortedDictionary<string, string> SpecialVariables = new(StringComparer.Ordinal)
    {
        ["$tosh"] = "The live ToSh runtime namespace root for config, script state, shell/session state, and host metadata.",
        ["_"] = "The current pipeline item in `where`, `each`, and similar contexts.",
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

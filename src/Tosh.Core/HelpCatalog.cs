using System.Text;

namespace Tosh.Core;

public static class HelpCatalog
{
    private static readonly IReadOnlyDictionary<string, LanguageHelpDefinition> LanguageTopics =
        new Dictionary<string, LanguageHelpDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["func"] = new(
                Category: "Language",
                Description: "Defines a user function with optional CLR type annotations.",
                Usage: "func <name>(<param[: Type]> ...) [-> Type] { <statements> } | func <name> => <command-pipeline>",
                Aliases: Array.Empty<string>(),
                Related: ["invoke", "return", "require", "forget"],
                Examples:
                [
                    "func greet() { echo noargs }; func greet(name) { echo hello $name }",
                    "func kind(value: int) { echo int }; func kind(value: string) { echo string }",
                    "func recent(days: TimeSpan) { ls -la | where Modified > ((date now) - $days) }",
                    "func stringifyCount() -> string { count }",
                    "func upper_input() { $tosh.Function.Input | each { _.ToUpper() } }",
                    "func ll => ls -la",
                    "var double = func(x) => ($x * 2)",
                    "invoke (func(x) => ($x * 2)) 21",
                    "func t1 => test1",
                    "func t1 => test1 $1 \"Jim\" $2",
                ],
                Notes: "Use `$tosh.Function.Input` inside a function body to consume piped values. Function call arguments are available through `$tosh.Function.Args`. Command-wrapper functions also receive pipeline input automatically. Use the '=>'-form only for simple command-wrapper functions. If the wrapper body references $1, $2, and so on, Tosh detects those positional parameters automatically. Otherwise a zero-parameter wrapper forwards its call arguments to the wrapped command automatically. Parameters are dynamic by default, but can opt into CLR types. Top-level named functions support overloading by arity and parameter annotations; defining the same callable shape again replaces that overload. `func(...)` also works as an anonymous function expression, which yields a callable value that you can pass around and execute with `invoke`."),
            ["var"] = new(
                Category: "Language",
                Description: "Declares a variable and stores the resulting CLR object without flattening it.",
                Usage: "var <name> [= <expression-or-pipeline>]",
                Aliases: Array.Empty<string>(),
                Related: ["global", "shy", "export", "func", "forget"],
                Examples:
                [
                    "var files = ls -la",
                    "var rng = new System.Random()",
                    "var person",
                ],
                Notes: "Use $name to reference a variable later in a command or expression."),
            ["new"] = new(
                Category: "Language",
                Description: "Constructs a new CLR object or ToSh named type instance.",
                Usage: "new <TypeName>(<arg>, ...)",
                Aliases: Array.Empty<string>(),
                Related: ["class", "record", "using", "require", "types"],
                Examples:
                [
                    "var rng = new System.Random()",
                    "var pt = new Point(2, 3)",
                    "var item = new Item(\"Bread\", 2, \"Food\")",
                    "var bread = new Inv.Item(\"Bread\", 2)",
                ],
                Notes: "ToSh requires `new` for instance construction. Use plain `Type.Member` or `Type.static_method(...)` for static access."),
            ["splat"] = new(
                Category: "Language",
                Description: "Expands a collection into separate command arguments.",
                Usage: "<command> ...$values",
                Aliases: ["spread"],
                Related: ["source", "func", "var", "xargs"],
                Examples:
                [
                    "var publish_args = [\"publish\", \"src/Tosh.Cli/Tosh.Cli.csproj\"]",
                    "dotnet ...$publish_args",
                    "source ./script.tosh ...$tosh.Script.Args",
                ],
                Notes: "Splatting is explicit and only works in command argument position. Use it with arrays, lists, tuples, or ranges when you want each item to become its own argv entry."),
            ["class"] = new(
                Category: "Language",
                Description: "Defines a ToSh class with properties, constructors, instance methods, and static methods.",
                Usage: "class <Name>(<param[: Type]> ...) { <members> } | class <Name> { prop ...; func ...; Name(...) { ... } }",
                Aliases: Array.Empty<string>(),
                Related: ["prop", "func", "module", "record", "enum", "require", "export"],
                Examples:
                [
                    "class Item(name: string, quantity: int, category: string) { prop Name: string = name; prop Quantity: int = quantity; prop Category: string = category }",
                    "export class Item { prop Name: string? = null; Item() { } }",
                ],
                Notes: "Class instances flow through the object pipeline like any other value. Use `shy` to hide internal members from public inspection and external access. Classes can customize iteration with `enumerate()` or `GetEnumerator()`, and can override CLR-style behavior with `ToString()`, `Equals(...)`, and `GetHashCode()`."),
            ["module"] = new(
                Category: "Language",
                Description: "Defines a named ToSh module object with its own lexical scope and exported surface.",
                Usage: "module <Name> { <statements> }",
                Aliases: Array.Empty<string>(),
                Related: ["require", "export", "class", "enum", "record"],
                Examples:
                [
                    "module Inventory { func names() { ls | get Name } }",
                    "module Inventory { class Item(name: string) { prop Name: string = name } }",
                ],
                Notes: "Module bodies execute in their own lexical scope. Declarations inside a module are exported by default; use `shy` for private/internal helpers."),
            ["enum"] = new(
                Category: "Language",
                Description: "Defines a named symbolic value type with optional underlying numeric storage.",
                Usage: "enum <Name> [: <Type>] { <Member> [= <value>] ... }",
                Aliases: Array.Empty<string>(),
                Related: ["record", "class", "module", "describe-type"],
                Examples:
                [
                    "enum StockState { Unknown, Low, Ok }",
                    "enum ExitCode: int { Ok = 0, Failed = 1 }",
                ],
                Notes: "Enum members are addressable through the enum name, like `StockState.Low`, and type annotations can bind from matching strings or numeric values."),
            ["record"] = new(
                Category: "Language",
                Description: "Defines a named data shape with positional construction, defaults, and structural equality.",
                Usage: "record <Name>(<field[: Type][?][= value]>, ...)",
                Aliases: Array.Empty<string>(),
                Related: ["class", "enum", "module", "var"],
                Examples:
                [
                    "record Item(name: string, quantity: int, category?: string = \"Food\")",
                    "var bread = new Item(\"Bread\", 2)",
                ],
                Notes: "Record instances are first-class shell objects. `{ ... }` still creates an anonymous dynamic record."),
            ["prop"] = new(
                Category: "Language",
                Description: "Declares a class property, computed member, or accessor-backed property.",
                Usage: "prop <Name>[: Type] [= <expression>] | prop <Name>[: Type] => <expression> | prop <Name>[: Type] { get => ...; set => ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "shy", "func"],
                Examples:
                [
                    "prop Name: string = name",
                    "prop IsLowStock: bool => $this.is_low_stock()",
                    "prop ClassName: string? { get => $this.internal_name; set => $this.internal_name = $value }",
                ],
                Notes: "A `=>` property is a computed getter. Accessor blocks run in class scope and can use `$this` and `$value`."),
            ["shy"] = new(
                Category: "Language",
                Description: "Keeps a declaration or class member internal to the current lexical scope or class.",
                Usage: "shy var <name> = ... | shy func <name>(...) { ... } | shy require <path> | shy using <namespace> | shy prop <Name> ...",
                Aliases: Array.Empty<string>(),
                Related: ["class", "prop", "func", "var", "require", "using", "global", "export"],
                Examples:
                [
                    "shy var count = 0",
                    "shy func helper() { echo hidden }",
                    "shy using System.Drawing = Drawing",
                    "shy require Inventory from ./inventory.tosh as Inv",
                    "shy prop internal_name => $\"item_{$this.Name.ToLower()}\"",
                    "shy func is_low_stock() -> bool { return ($this.Quantity < 5) }",
                ],
                Notes: "For declarations, `shy` keeps the name in the current lexical scope instead of publishing it outward. For class members, shy members are callable and readable from inside the class through `$this`, but they stay hidden from normal `inspect`, `get`, and public member access."),
            ["static"] = new(
                Category: "Language",
                Description: "Marks a class method as belonging to the class itself instead of an instance.",
                Usage: "static func <name>(...) [-> Type] { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "func", "prop"],
                Examples:
                [
                    "static func named(name: string) -> Item { return new Item($name) }",
                ],
                Notes: "Call static members through the class name, like `Item.named(\"bread\")`."),
            ["global"] = new(
                Category: "Language",
                Description: "Publishes a declaration to the session-wide scope instead of the current lexical scope.",
                Usage: "global var <name> = ... | global func <name>(...) { ... } | global func <name> => <command-pipeline>",
                Aliases: Array.Empty<string>(),
                Related: ["var", "shy", "export", "require"],
                Examples:
                [
                    "global var root = pwd",
                    "global func greet() { echo hi }",
                ],
                Notes: "Global declarations are visible after the current function, block, or required module finishes."),
            ["export"] = new(
                Category: "Language",
                Description: "On declarations, publishes a module value or command outward; as a command, exports an environment variable.",
                Usage: "export var <name> = ... | export func <name>(...) { ... } | export func <name> => <command-pipeline> | export <ENV_NAME> [value]",
                Aliases: Array.Empty<string>(),
                Related: ["require", "global", "shy", "env"],
                Examples:
                [
                    "export func names() { ls | get Name }",
                    "export PATH \"/usr/local/bin\"",
                ],
                Notes: "Inside a module, declarations are already exported by default. Use `export` when you want a declaration in a nested scope to publish outward explicitly."),
            ["using"] = new(
                Category: "Language",
                Description: "Imports CLR namespaces and aliases into the current lexical scope.",
                Usage: "[shy|global] using <namespace> | [shy|global] using <namespace> as <alias> | [shy|global] using <namespace> = <alias>",
                Aliases: Array.Empty<string>(),
                Related: ["require", "load-assembly", "types", "help"],
                Examples:
                [
                    "using System.IO",
                    "using System.IO = IO",
                    "func demo() { using System.Drawing = Drawing; echo new Drawing.Point(2, 3).X }",
                ],
                Notes: "Use 'require' for ToSh files and modules; 'using' is CLR-only. Plain `using` is lexical by default, `shy using` makes that explicit, and `global using` publishes to the session scope."),
            ["require"] = new(
                Category: "Language",
                Description: "Loads a ToSh script/module, assembly, project, or native shared library once per session, then imports the requested surface into the current lexical scope.",
                Usage: "[shy|global] require <path> | [shy|global] require <Name> from <path> [as <Alias>] | [shy|global] require { <Name> [, ...] } from <path> | [shy|global] require native <library> [as <Alias>]",
                Aliases: Array.Empty<string>(),
                Related: ["source", "using", "module", "class", "enum", "record", "bind", "export"],
                Examples:
                [
                    "require ./common.tosh",
                    "require Inventory from ./inventory.tosh as Inv",
                    "require { Inventory, Reporting } from ./inventory.tosh",
                    "require ./lib/MyTools.dll",
                    "require ./src/MyProject/MyProject.csproj",
                    "require native libc.so.6 as LibC",
                    "global require ~/.config/tosh/profile.tosh",
                ],
                Notes: "Require caches the loaded target once per session. For `.tosh` files, exports are imported lexically by default; use `shy require` when you want to be explicit about keeping imports local to the current scope, or `global require` when you want the imported names to remain visible after the current scope ends. `require native ... as Name` creates a module object that you can populate with `bind Name { func ... }`. Use 'source' to rerun a file each time."),
            ["bind"] = new(
                Category: "Interop",
                Description: "Binds native shared-library exports onto a required native module object.",
                Usage: "bind <Module> { func <Name>([[out|ref] <name>:] <type> [, ...]) [-> <type>] [as <symbol>] [callconv <name>] } | bind native <library> [as <Module>] { func <Name>([[out|ref] <name>:] <type> [, ...]) [-> <type>] [as <symbol>] [callconv <name>] }",
                Aliases: Array.Empty<string>(),
                Related: ["require", "using", "constructors"],
                Examples:
                [
                    "require native libc.so.6 as LibC",
                    "bind LibC { func abs(int) -> int }",
                    "bind LibC { func myAbs(value: int) -> int as \"abs\" }",
                    "bind LibC { func time(out value: long) -> long }",
                    "bind native libc.so.6 as LibC { func abs(int) -> int }",
                    "bind native libc.so.6 as LibC { func strlen(string) -> nuint }",
                    "bind native libc.so.6 as LibC { func getenv(string) -> cstring }",
                    "bind User32 { func MessageBoxW(nint, string, string, uint) -> int callconv stdcall }",
                ],
                Notes: "Bind works with modules created by `require native`, or it can load a native library inline with `bind native ... as Name { ... }`. Bound functions become callable as module members like `LibC.abs(-5)`. Use `cstring` for borrowed NUL-terminated C-string returns, and `nint` / `nuint` (or `ptr` / `uptr`) for raw pointer-sized values. Plain `string` is supported for native parameters, but native return strings require explicit ownership semantics, so ToSh asks you to use `cstring` instead. `out` and `ref` parameters return updated values after the call: if there is one by-ref parameter and no normal return value, ToSh yields that value directly; otherwise it yields a record with `ReturnValue` plus one field per by-ref parameter. Passing a native buffer lets the call write back into unmanaged memory directly. Calling conventions default to `cdecl` and can be overridden with `callconv stdcall`, `callconv thiscall`, `callconv fastcall`, or `callconv winapi`."),
            ["alloc"] = new(
                Category: "Interop",
                Description: "Allocates an unmanaged native buffer by byte count or interop type size.",
                Usage: "alloc <name> = <bytes | type-name> | alloc <bytes | type-name>",
                Aliases: ["native-alloc"],
                Related: ["bind", "forget", "read-buffer", "write-buffer", "size-of"],
                Examples:
                [
                    "alloc buffer = 256",
                    "alloc fileTimeBuffer = System.Runtime.InteropServices.ComTypes.FILETIME",
                    "var scratch = (alloc 256)",
                ],
                Notes: "Use the statement form when you want a named buffer immediately. Use `forget $buffer` or `forget buffer` to release it when you are done. The older `native-alloc` helper name still works."),
            ["native-free"] = new(
                Category: "Interop",
                Description: "Frees unmanaged buffers created by native-alloc.",
                Usage: "native-free [buffer ...]",
                Aliases: Array.Empty<string>(),
                Related: ["alloc", "read-buffer", "write-buffer", "forget"],
                Examples:
                [
                    "native-free $buffer",
                    "$buffers | native-free",
                ],
                Notes: "This compatibility helper still works, but `forget` is now the preferred way to release named native buffers. It does not attempt to free arbitrary raw pointers returned by external libraries."),
            ["read-buffer"] = new(
                Category: "Interop",
                Description: "Reads a C string, byte range, or native scalar/struct-layout value from native memory.",
                Usage: "read-buffer <cstring|bytes|type-name> [buffer|pointer] [length] [offset]",
                Aliases: ["native-read"],
                Related: ["alloc", "write-buffer", "size-of", "offset-of"],
                Examples:
                [
                    "read-buffer cstring $buffer",
                    "read-buffer bytes $buffer 16",
                    "read-buffer long $buffer",
                    "read-buffer System.Runtime.InteropServices.ComTypes.FILETIME $buffer",
                ],
                Notes: "Use `cstring` for NUL-terminated borrowed C strings, `bytes` when you want a raw byte array, a primitive/enum/pointer-sized type when you want a single native scalar, or a struct with sequential/explicit layout when you want a marshalled value. This is also the easiest way to inspect the results of `out` and `ref` native calls that write into an allocated buffer. The older `native-read` name still works."),
            ["write-buffer"] = new(
                Category: "Interop",
                Description: "Writes a C string, byte sequence, or struct-layout value into native memory.",
                Usage: "write-buffer <buffer|pointer> <value> [offset]",
                Aliases: ["native-write"],
                Related: ["alloc", "read-buffer", "offset-of"],
                Examples:
                [
                    "write-buffer $buffer \"toast\"",
                    "write-buffer $buffer [1, 2, 3, 4]",
                    "write-buffer $buffer (new System.Runtime.InteropServices.ComTypes.FILETIME())",
                ],
                Notes: "Strings are written as NUL-terminated C strings. Struct values should have sequential or explicit layout. This pairs naturally with `bind` when a native export expects an `out` or `ref` buffer-backed struct. The older `native-write` name still works."),
            ["size-of"] = new(
                Category: "Interop",
                Description: "Returns the unmanaged size of a supported native interop type.",
                Usage: "size-of <type-name> [type-name ...]",
                Aliases: ["native-sizeof"],
                Related: ["offset-of", "alloc", "bind"],
                Examples:
                [
                    "size-of int",
                    "size-of System.Runtime.InteropServices.ComTypes.FILETIME",
                ],
                Notes: "This uses the same native layout rules as bind and the buffer helpers. The older `native-sizeof` name still works."),
            ["offset-of"] = new(
                Category: "Interop",
                Description: "Returns the unmanaged field offset for a sequential or explicit-layout struct.",
                Usage: "offset-of <type-name> <field-name> | offset-of <type-name>.<field-name>",
                Aliases: ["native-offsetof"],
                Related: ["size-of", "read-buffer", "write-buffer"],
                Examples:
                [
                    "offset-of System.Runtime.InteropServices.ComTypes.FILETIME dwLowDateTime",
                    "offset-of System.Runtime.InteropServices.ComTypes.FILETIME.dwHighDateTime",
                ],
                Notes: "Use this when you need to reason about native layout directly, especially for pointer arithmetic or validating a struct definition. The older `native-offsetof` name still works."),
            ["redirection"] = new(
                Category: "Shell",
                Description: "Redirects pipeline output and native process streams explicitly, without overloading '<' or '>' comparisons.",
                Usage: "<pipeline> out> <path> | <pipeline> out>> <path> | <pipeline> err> <path> | <pipeline> o+e> <path> | <pipeline> e+o> <path> | <<< <value> | <pipeline>",
                Aliases: Array.Empty<string>(),
                Related: ["as-file", "glob", "match", "new"],
                Examples:
                [
                    "echo hello out> ./hello.txt",
                    "/bin/sh -c \"printf 'out\\n'; printf 'err\\n' >&2\" out> stdout.txt err> stderr.txt",
                    "/bin/sh -c \"printf 'out\\n'; printf 'err\\n' >&2\" out> combined.txt err>> combined.txt",
                    "/bin/sh -c \"printf 'out\\n'; printf 'err\\n' >&2\" o+e> combined.txt",
                    "/bin/sh -c \"printf 'out\\n'; printf 'err\\n' >&2\" out> stdout.txt err> stderr.txt &",
                    "/bin/sh -c \"printf one\\n\" out>> ./combined.txt",
                    "<<< \"alpha\\nbeta\" | /bin/cat",
                ],
                Notes: "ToSh keeps '<' and '>' as expression comparison operators. Shell redirection stays explicit with forms like `out>`, `err>`, `o+e>`, `e+o>`, and their `>>` append variants. Combined redirects write stdout followed by stderr deterministically, and redirecting `out>` and `err>` into the same file follows that same stable ordering. `<<<` feeds a value into the pipeline as stdin-style input, which is especially useful with native commands. Background native pipelines launched with `&` honor the same explicit redirection forms."),
            ["if"] = new(
                Category: "Control Flow",
                Description: "Evaluates a condition and runs one block, optionally followed by else if or else blocks.",
                Usage: "if (<condition>) { <statements> } [else if (<condition>) { ... }] [else { ... }]",
                Aliases: Array.Empty<string>(),
                Related: ["where", "while", "until", "for", "return", "switch"],
                Examples:
                [
                    "if ((ps | count) > 0) { writeline \"processes visible\" } else { writeline \"none\" }",
                ],
                Notes: "Conditions use the same expression and operator model as the rest of Tosh."),
            ["for"] = new(
                Category: "Control Flow",
                Description: "Iterates over an enumerable value and binds each item to a loop variable.",
                Usage: "for <name> in (<expression-or-pipeline>) { <statements> }",
                Aliases: Array.Empty<string>(),
                Related: ["each", "while", "break", "continue"],
                Examples:
                [
                    "for proc in (ps | sort Memory | reverse | first 3) { writeline $proc.Name }",
                    "for item in ($items) { echo $item }",
                ],
                Notes: "Loop variables are scoped to the block. Lists, arrays, sets, tuples, and ranges iterate naturally. ToSh classes can customize iteration with an instance `enumerate()` method; `GetEnumerator()` is accepted too."),
            ["while"] = new(
                Category: "Control Flow",
                Description: "Runs a block repeatedly while a condition evaluates to true.",
                Usage: "while (<condition>) { <statements> }",
                Aliases: Array.Empty<string>(),
                Related: ["until", "for", "if", "break", "continue"],
                Examples:
                [
                    "while (($count < 3)) { writeline $count; $count += 1 }",
                ],
                Notes: "Use break and continue to control loop flow."),
            ["until"] = new(
                Category: "Control Flow",
                Description: "Runs a block repeatedly until a condition evaluates to true.",
                Usage: "until (<condition>) { <statements> }",
                Aliases: Array.Empty<string>(),
                Related: ["while", "for", "break", "continue"],
                Examples:
                [
                    "until (($done == true)) { writeline \"waiting\" }",
                ],
                Notes: "Until is the shell-friendly inverse of while."),
            ["return"] = new(
                Category: "Control Flow",
                Description: "Exits the current function or top-level script early, optionally yielding a value.",
                Usage: "return [<expression-or-command>]",
                Aliases: Array.Empty<string>(),
                Related: ["func", "throw", "break", "continue", "if", "try"],
                Examples:
                [
                    "return",
                    "return get Name",
                    "return String.Join(\" \", [\"hello\", \"world\"])",
                ],
                Notes: "Return can bubble out from nested blocks and loops."),
            ["throw"] = new(
                Category: "Control Flow",
                Description: "Raises an error value that can be handled by a surrounding try/catch.",
                Usage: "throw [<expression-or-command>]",
                Aliases: Array.Empty<string>(),
                Related: ["try", "return", "if"],
                Examples:
                [
                    "throw \"something went wrong\"",
                    "throw (new Tosh.Core.CommandFailure(\"failed\"))",
                ],
                Notes: "Unhandled throws become shell diagnostics at the top level."),
            ["try"] = new(
                Category: "Control Flow",
                Description: "Runs a block and optionally handles failures with catch/finally blocks.",
                Usage: "try { <statements> } [catch [(err)] { <statements> }] [finally { <statements> }]",
                Aliases: Array.Empty<string>(),
                Related: ["throw", "if", "return"],
                Examples:
                [
                    "try { throw \"boom\" } catch (err) { echo $err }",
                    "try { risky-command } finally { writeline \"done\" }",
                ],
                Notes: "Catch receives the thrown value or error object when a variable name is provided."),
            ["switch"] = new(
                Category: "Control Flow",
                Description: "Matches a value against case expressions and runs the first matching block.",
                Usage: "switch (<value>) { case <value> { ... } [case <value> { ... }] [default { ... }] }",
                Aliases: Array.Empty<string>(),
                Related: ["match-expr", "if", "where", "try"],
                Examples:
                [
                    "switch ($kind) { case file { echo file } case dir { echo dir } default { echo other } }",
                ],
                Notes: "Switch uses the same equality rules as the rest of ToSh. For expression-oriented branching, prefer `match (<value>) { pattern => result; default => fallback }`."),
            ["match-expr"] = new(
                Category: "Control Flow",
                Description: "Matches a value against ordered arms and returns the selected arm result.",
                Usage: "match (<value>) { <pattern> [if (<guard>)] => <result>; default => <fallback> }",
                Aliases: ["match-expression"],
                Related: ["switch", "if", "ternary"],
                Examples:
                [
                    "var label = match ($kind) { file => \"file\"; dir => \"dir\"; default => \"other\" }",
                    "match ($state) { StockState.Low if (($count < 3)) => \"restock\"; default => \"ok\" }",
                    "match ($kind) { file => { echo file }; default => { echo other } }",
                ],
                Notes: "Match arms are checked in order. Use `default => ...` as the fallback arm, and guards use `if (<condition>)`."),
            ["ternary"] = new(
                Category: "Language",
                Description: "Chooses between two expressions based on a condition.",
                Usage: "<condition> ? <when-true> : <when-false>",
                Aliases: Array.Empty<string>(),
                Related: ["if", "??", "where"],
                Examples:
                [
                    "var label = ($count > 0) ? \"items\" : \"empty\"",
                    "var name = ($user != null) ? $user.Name : \"guest\"",
                ],
                Notes: "Ternary evaluation is lazy: only the selected branch runs. Ternary binds more loosely than `??`, so `null ?? true ? yes : no` reads as `(null ?? true) ? yes : no`."),
            ["break"] = new(
                Category: "Control Flow",
                Description: "Exits the current loop immediately.",
                Usage: "break",
                Aliases: Array.Empty<string>(),
                Related: ["continue", "for", "while", "until", "each"],
                Examples:
                [
                    "for item in (echo one two three) { echo $item; break }",
                ],
                Notes: "Break currently applies inside for, while, and each-driven block execution."),
            ["continue"] = new(
                Category: "Control Flow",
                Description: "Skips the rest of the current loop iteration and continues with the next one.",
                Usage: "continue",
                Aliases: Array.Empty<string>(),
                Related: ["break", "for", "while", "until", "each"],
                Examples:
                [
                    "echo one skip two | each { if ((_ == skip)) { continue }; echo _ }",
                ],
                Notes: "Continue works in loops and each blocks."),
        };

    internal static readonly IReadOnlyDictionary<string, string[]> ExamplesByName =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = ["help search json", "help ls", "help related where"],
            ["apropos"] = ["apropos json", "apropos loop"],
            ["cat"] = ["cat README.md", "echo alpha beta | cat -", "cat -n README.md"],
            ["wc"] = ["wc README.md", "wc -lwm README.md", "echo one two three | wc"],
            ["touch"] = ["touch notes.txt", "touch -c -a notes.txt", "touch -d 2026-03-28T12:00:00 notes.txt"],
            ["mv"] = ["mv old.txt new.txt", "mv -n *.txt archive/", "mv -t archive alpha.txt beta.txt"],
            ["env"] = ["env PATH", "echo $env.PATH", "env FOO=bar env FOO | get Value", "env -u PATH env PATH | get IsSet"],
            ["exec"] = ["exec tosh", "exec zsh", "exec /bin/sh -c \"echo hi\""],
            ["http"] = ["http get https://example.com --as text", "http post https://example.com/api --json { Name = \"Toast\" } --as response", "http request GET https://example.com | http send --as response"],
            ["ip"] = ["ip addr", "ip link | where _.IsUp", "ip route | where _.IsDefault"],
            ["systemctl"] = ["systemctl", "systemctl list-unit-files --type service | where _.Enabled", "systemctl status sshd.service | get { Id, ActiveState, MainPID, RecentLogCount }"],
            ["journalctl"] = ["journalctl -n 20", "journalctl -u sshd.service | first 10", "journalctl --since yesterday | where _.Priority <= 3"],
            ["loginctl"] = ["loginctl", "loginctl list-users | where _.State == active", "loginctl show-user 1000 | get { UID, Name, State, Sessions }"],
            ["hostnamectl"] = ["hostnamectl", "hostnamectl --show Hostname,OperatingSystem,KernelRelease", "hostnamectl | get { Hostname, MachineID, BootID }"],
            ["networkctl"] = ["networkctl", "networkctl | where _.Setup == unmanaged", "networkctl --show Link,Operational,Setup,Managed"],
            ["lscpu"] = ["lscpu", "lscpu -e | first 5", "lscpu -C -B | get { Name, Level, OneSize, AllSize }"],
            ["lsfd"] = ["lsfd | first 5", "lsfd -i4 | where _.Type == SOCK", "lsfd --summary=only"],
            ["lsipc"] = ["lsipc", "lsipc -m | first 5", "lsipc -g -m | get { Resource, Limit, Used, UsePercent }"],
            ["ls"] = ["ls -la", "ls -R --group-directories-first", "ls -l --time access | where _.Type == file | get { Name, Accessed }"],
            ["df"] = ["df --total", "df -t ext4 --show FileSystem,Type,UsePercent,MountedOn", "df . | get { FileSystem, MountedOn }"],
            ["du"] = ["du -s .", "du -a -c --time", "du -x ./projects | get { Name, Size, Modified }"],
            ["stat"] = ["stat ./README.md", "stat -L ./link.txt", "stat -f . | get { RequestedPath, FileSystem, MountedOn }"],
            ["date"] = ["date now -d -t", "date parse 2026-03-29T12:34:56Z -d", "date parse 2026-03-29T12:34:56Z | cast timeonly"],
            ["guid"] = ["guid", "guid new v7", "guid info 550e8400-e29b-41d4-a716-446655440000"],
            ["invoke"] = ["invoke (func(x) => ($x * 2)) 21", "var add = func(x, y) => ($x + $y); invoke $add 3 4", "var scale = func(x) => ($x * $factor); invoke $scale 7"],
            ["partial"] = ["var add = func(x, y) => ($x + $y); var inc = partial $add 1; invoke $inc 41", "var join3 = func(a, b, c) => ($\"{$a}-{$b}-{$c}\"); invoke (partial $join3 alpha beta) gamma"],
            ["curry"] = ["var add3 = func(a, b, c) => ($a + $b + $c); var curried = curry $add3", "var step1 = invoke $curried 1; var step2 = invoke $step1 2; invoke $step2 39"],
            ["summarize"] = ["df | summarize", "df | summarize _.Used", "ps | summarize --avg Memory --max Memory"],
            ["summary"] = ["summary _.Used", "seq 5 | summary --count", "ps | summary --avg Memory --max Memory"],
            ["map"] = ["echo 1 2 3 | map func(x) => ($x * 2)", "ls | map { _.Name }", "ps | map func(p) => ($p.Name.ToUpper())"],
            ["filter"] = ["echo 1 2 3 4 | filter func(x) => ((($x % 2) == 0))", "ls | filter { _.Type == file }", "findmnt -l | filter func(m) => ($m.FsType == \"ext4\")"],
            ["reduce"] = ["echo 1 2 3 4 | reduce 0 func(acc, x) => ($acc + $x)", "echo one two three | reduce \"\" { $acc + _.Substring(0, 1) }", "ls | reduce 0 { $acc + _.Size }"],
            ["any"] = ["ps | any func(p) => ($p.Name == \"sshd\")", "echo 1 2 3 | any func(x) => ($x == 2)"],
            ["all"] = ["echo 2 4 6 | all func(x) => ((($x % 2) == 0))", "ls | all { _.Exists }"],
            ["none"] = ["echo 1 2 3 | none func(x) => ($x > 10)", "ls | none { _.Type == link }"],
            ["foreach"] = ["echo one two | foreach func(x) => ($x.ToUpper())", "ls | foreach { _.Name }"],
            ["collect"] = ["echo 1 2 3 | collect", "ls *.cs | collect", "findmnt -l | where _.FsType == ext4 | collect"],
            ["read-file"] = ["read-file ./notes.txt", "ls *.md | first | read-file"],
            ["read-lines"] = ["read-lines ./notes.txt", "read-lines ./notes.txt | grep error"],
            ["write-file"] = ["write-file ./notes.txt \"hello world\"", "echo alpha beta | write-file ./notes.txt"],
            ["append-file"] = ["append-file ./notes.txt \" more\"", "echo \"tail line\" | append-file ./notes.txt"],
            ["read-bytes"] = ["read-bytes ./image.bin", "read-bytes ./image.bin | type-of"],
            ["write-bytes"] = ["write-bytes ./data.bin [1, 2, 3, 255]", "read-bytes ./source.bin | write-bytes ./copy.bin"],
            ["open-file"] = ["open-file ./notes.txt", "open-file --write ./notes.txt", "open-file --binary --append ./data.bin"],
            ["read-from"] = ["$handle | read-from 64", "read-from $handle 4096"],
            ["read-line-from"] = ["$handle | read-line-from", "read-line-from $handle"],
            ["read-to-end"] = ["$handle | read-to-end", "read-to-end $handle"],
            ["write-to"] = ["write-to $handle hello world", "echo alpha beta | write-to $handle"],
            ["write-line-to"] = ["write-line-to $handle hello world", "echo alpha beta | write-line-to $handle"],
            ["flush"] = ["flush $handle", "echo $handle | flush"],
            ["close"] = ["close $handle", "echo $handle | close"],
            ["seek"] = ["seek $handle 0 begin", "$handle | seek 128 current"],
            ["position"] = ["position $handle", "echo $handle | position"],
            ["length"] = ["length $handle", "echo $handle | length"],
            ["copy-to"] = ["copy-to $source $target", "$source | copy-to $target"],
            ["ps"] = ["ps -f --sort -Id", "ps -u root | get { Name, Id, User }", "ps | sort Memory | reverse | first 5"],
            ["where"] = ["ls -la | where _.Type == file", "ls -la | where func(item) => ($item.Name.ToLower().EndsWith(\".md\"))"],
            ["each"] = ["echo one two | each { _.ToUpper() }", "DriveInfo.GetDrives() | each func(d) => ($d.Name)"],
            ["get"] = ["ls -la | get Name", "ps | get { Name, PID, Memory }", "echo 1 2 3 | get func(x) => ($x * 2)"],
            ["select"] = ["echo 1 2 3 | select func(x) => ($x * 2)", "ls | select func(item) => ($item.Name)"],
            ["sort"] = ["ps | sort Memory", "ls -la | sort Modified | reverse", "ps | sort func(p) => ($p.Name.Length)"],
            ["group-by"] = ["ls | group-by Extension", "ps | group-by func(p) => ($p.Name.Substring(0, 1))"],
            ["take-while"] = ["echo 1 2 3 4 | take-while { _ < 3 }", "echo 1 2 3 4 | take-while func(x) => ($x < 3)"],
            ["skip-while"] = ["echo 1 2 3 4 | skip-while { _ < 3 }", "echo 1 2 3 4 | skip-while func(x) => ($x < 3)"],
            ["inspect"] = ["ls -la | first | inspect", "new System.Random() | inspect -a", "new System.Random() | inspect --flat"],
            ["from"] = ["echo \"{\\\"name\\\":\\\"toast\\\"}\" | from json", "curl https://example/api | from json | flatten", "cat data.toml | from toml", "cat data.csv | from csv"],
            ["to"] = ["ls | to json", "ls | to csv", "ls | to toml", "ls | to json --compact"],
            ["parse"] = ["ping -c 3 localhost | parse \"time=(?<time_ms>[0-9.]+) ms\"", "echo \"PID=42\" | parse \"PID=(?<Pid>[0-9]+)\"", "echo \"first\\nsecond\" | parse -am \"^(?<Value>\\\\w+)$\" | get Value"],
            ["grep"] = ["echo one two three | grep tw", "echo \"Alpha\" | grep -i \"^alpha$\"", "grep -F literal README.md"],
            ["match"] = ["echo \"PID=42\" | match \"PID=(?<Pid>[0-9]+)\" | get Pid", "echo \"Alpha\" | match -i \"^alpha$\""],
            ["replace"] = ["echo alpha-beta | replace beta BETA", "echo \"A1 B2\" | replace -r \"[0-9]\" \"#\""],
            ["split"] = ["echo \"alpha,beta,gamma\" | split \",\"", "echo \"alpha,beta;gamma\" | split -r \"[,;]\""],
            ["find"] = ["find . -name *.tosh", "find . -regex \".*\\\\.tosh$\"", "find . -iregex \".*readme.*\""],
            ["types"] = ["types System.String", "types list", "types map | where _.Namespace == ToSh"],
            ["members"] = ["members string", "DateTime.Now | members"],
            ["constructors"] = ["constructors System.String | first 5", "constructors list<int>", "constructors dict<string, int>"],
            ["has-prop"] = ["$obj | has-prop Name", "has-prop $obj Name"],
            ["has-method"] = ["$obj | has-method ToString", "has-method $obj ToString"],
            ["get-props"] = ["$obj | get-props", "get-props $obj"],
            ["get-methods"] = ["$obj | get-methods", "get-methods $obj"],
            ["get-prop"] = ["$obj | get-prop $propName", "get-prop $obj Name"],
            ["set-prop"] = ["$obj | set-prop Name \"value\"", "set-prop $obj Name \"value\""],
            ["del-prop"] = ["$obj | del-prop Name", "del-prop $obj Name"],
            ["call-method"] = ["echo hello | call-method ToUpper", "call-method $obj MethodName arg1"],
            ["clone"] = ["$obj | clone", "clone $obj"],
            ["raw"] = ["echo 1317 | raw", "echo System.DayOfWeek.Friday | raw"],
            ["cast"] = ["echo [1, 2, 3] | cast list<int>", "echo 42 | cast string"],
            ["view"] = ["view dateonly scalar relative", "view timeonly table 24h", "view duration table seconds"],
            ["chmod"] = ["chmod +x script.sh", "chmod 755 script.sh", "chmod u+rw,go-w file.txt", "chmod -R a+r ./docs"],
            ["new"] = ["var rng = new System.Random()", "var items = new list<String>(\"one\", \"two\")", "new System.Text.StringBuilder(\"hello\").Append(\" world\").ToString()"],
            ["styled"] = ["styled \"hello\" --fg cyan --bold", "styled \"warning\" --fg yellow --bg red"],
            ["prompt-time"] = ["prompt-time --dim", "prompt-time --format \"HH:mm:ss\" --fg gray"],
            ["prompt-dir"] = ["prompt-dir --fg blue --bold", "prompt-dir --fg yellow --depth 2"],
            ["prompt-git"] = ["prompt-git", "prompt-git --fg bright-green --bold"],
            ["prompt-userhost"] = ["prompt-userhost --dim", "prompt-userhost --fg gray"],
            ["prompt-history"] = ["prompt-history", "prompt-history 432 --fg gray --dim"],
            ["prompt-jobs"] = ["prompt-jobs", "prompt-jobs 3 --fg yellow --bold"],
            ["prompt-duration"] = ["prompt-duration", "prompt-duration 2.5s --threshold-ms 250"],
            ["prompt-exit"] = ["prompt-exit", "prompt-exit 7 --fg red --bold"],
            ["prompt-text"] = ["prompt-text \"> \" --fg cyan", "prompt-text \"::\" --fg gray --dim"],
            ["prompt-newline"] = ["prompt-newline"],
        };

    internal static readonly IReadOnlyDictionary<string, HelpDetailDefinition> CommandDetailsByName =
        new Dictionary<string, HelpDetailDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = new(
                Arguments:
                [
                    new("topic", "The command, language topic, type, or external executable to describe.", Required: false),
                    new("--cli [query]", "Opens the inline fuzzy tree browser, optionally seeded with an initial query or topic.", Required: false),
                    new("browse [query]", "Opens the full-screen help browser, optionally filtered by an initial query.", Required: false),
                    new("search <query>", "Searches help topics by name, alias, category, and description.", Required: false),
                    new("related <topic>", "Shows related topics for the given command or language feature.", Required: false),
                    new("categories", "Lists help categories and topic counts.", Required: false),
                ],
                PipelineInput: new(false, true, false, false, "With no explicit args, piped scalar values are treated as help topics. `search` and `related` also consume piped query/topic text."),
                Output: "Produces help summaries, full help topics, search results, category rows, launches the inline browser with `--cli`, or returns an interactive browser request for `help browse` depending on the form.",
                Examples:
                [
                    new("help ls", "Describe a command"),
                    new("help search regex", "Search help"),
                    new("help --cli regex", "Open the inline fuzzy help browser"),
                    new("help browse grep", "Open the help browser"),
                ]),
            ["raw"] = new(
                Arguments:
                [
                    new("value ...", "Optional explicit values to emit as plain text instead of rich object display.", Required: false),
                ],
                PipelineInput: new(true, true, true, true, "Consumes pipeline values and emits one plain-text line per input item."),
                Output: "Returns ShellTextLine values so the final display stays plain text instead of tables or record views.",
                Examples:
                [
                    new("echo 1317 | raw", "Show a scalar without rich boxing"),
                    new("echo System.DayOfWeek.Friday | raw", "Show an enum's raw CLR string form"),
                ]),
            ["invoke"] = new(
                Arguments:
                [
                    new("callable", "A callable value such as an anonymous `func(...)` expression or other shell callable object."),
                    new("arg ...", "Optional positional arguments passed to the callable.", Required: false),
                ],
                PipelineInput: new(false, false, false, false, "`invoke` is explicit-argument based for now. It does not automatically feed the current pipeline into the callable."),
                Output: "Returns whatever values the callable yields.",
                Examples:
                [
                    new("invoke (func(x) => ($x * 2)) 21", "Invoke an inline anonymous function"),
                    new("var add = func(x, y) => ($x + $y); invoke $add 3 4", "Store a lambda and invoke it later"),
                    new("var factor = 3; var scale = func(x) => ($x * $factor); invoke $scale 7", "Invoke a closure that captures lexical state"),
                ]),
            ["partial"] = new(
                Arguments:
                [
                    new("callable", "A callable value to partially apply."),
                    new("arg ...", "Leading positional arguments to bind ahead of time.", Required: false),
                ],
                PipelineInput: new(false, false, false, false, "`partial` is explicit-argument based and returns a new callable value."),
                Output: "Returns a callable value that prepends the bound arguments before invoking the original callable.",
                Examples:
                [
                    new("var add = func(x, y) => ($x + $y); var inc = partial $add 1; invoke $inc 41", "Bind the first argument of a callable"),
                    new("invoke (partial (func(a, b, c) => ($\"{$a}-{$b}-{$c}\")) alpha beta) gamma", "Bind multiple leading arguments"),
                ]),
            ["curry"] = new(
                Arguments:
                [
                    new("callable", "A fixed-arity callable value to curry."),
                ],
                PipelineInput: new(false, false, false, false, "`curry` returns a callable that accumulates arguments until the original callable is saturated."),
                Output: "Returns a curried callable. Calling it with too few arguments returns another callable; calling it with enough arguments runs the original callable.",
                Examples:
                [
                    new("var add3 = func(a, b, c) => ($a + $b + $c); var curried = curry $add3", "Create a curried callable"),
                    new("var step1 = invoke $curried 1; var step2 = invoke $step1 2; invoke $step2 39", "Invoke a curried callable one step at a time"),
                ]),
            ["map"] = new(
                Arguments:
                [
                    new("callable|block", "A lambda or block that transforms each input item into exactly one output value."),
                ],
                PipelineInput: new(true, true, false, false, "Consumes the current pipeline and emits one transformed value per input item."),
                Output: "Returns one transformed value for each input item.",
                Examples:
                [
                    new("echo 1 2 3 | map func(x) => ($x * 2)", "Transform values with a lambda"),
                    new("ls | map { _.Name }", "Transform values with a block"),
                ]),
            ["filter"] = new(
                Arguments:
                [
                    new("callable|block", "A lambda or block predicate that returns boolean values."),
                ],
                PipelineInput: new(true, true, false, false, "Consumes the current pipeline and keeps only items whose predicate evaluates to true. Tree-shaped inputs are pruned like `where`."),
                Output: "Returns the input items that matched the predicate.",
                Examples:
                [
                    new("echo 1 2 3 4 | filter func(x) => ((($x % 2) == 0))", "Filter with a lambda"),
                    new("ls | filter { _.Type == file }", "Filter with a block"),
                ]),
            ["reduce"] = new(
                Arguments:
                [
                    new("seed", "The initial accumulator value."),
                    new("callable|block", "A lambda or block that combines the current accumulator with each input item and returns the next accumulator."),
                ],
                PipelineInput: new(true, true, false, false, "Consumes the current pipeline from left to right and folds it into one final value."),
                Output: "Returns the final accumulator value. On an empty input stream, `reduce` returns the seed unchanged.",
                Examples:
                [
                    new("echo 1 2 3 4 | reduce 0 func(acc, x) => ($acc + $x)", "Fold numeric values"),
                    new("echo one two three | reduce \"\" { $acc + _.Substring(0, 1) }", "Fold with a block"),
                ]),
            ["any"] = new(
                Arguments:
                [
                    new("callable|block", "A lambda or block predicate that returns boolean values."),
                ],
                PipelineInput: new(true, true, false, false, "Consumes the current pipeline and stops at the first matching item."),
                Output: "Returns `true` if any item matches the predicate; otherwise `false`.",
                Examples:
                [
                    new("echo 1 2 3 | any func(x) => ($x == 2)", "Check whether any item matches"),
                ]),
            ["all"] = new(
                Arguments:
                [
                    new("callable|block", "A lambda or block predicate that returns boolean values."),
                ],
                PipelineInput: new(true, true, false, false, "Consumes the current pipeline and stops at the first non-matching item."),
                Output: "Returns `true` if every item matches the predicate; otherwise `false`.",
                Examples:
                [
                    new("echo 2 4 6 | all func(x) => ((($x % 2) == 0))", "Check whether all items match"),
                ]),
            ["none"] = new(
                Arguments:
                [
                    new("callable|block", "A lambda or block predicate that returns boolean values."),
                ],
                PipelineInput: new(true, true, false, false, "Consumes the current pipeline and stops at the first matching item."),
                Output: "Returns `true` if no items match the predicate; otherwise `false`.",
                Examples:
                [
                    new("echo 1 2 3 | none func(x) => ($x > 10)", "Check whether no items match"),
                ]),
            ["foreach"] = new(
                Arguments:
                [
                    new("callable|block", "A lambda or block executed once per input item."),
                ],
                PipelineInput: new(true, true, false, false, "`foreach` is an alias of `each` and preserves the same callable/block semantics."),
                Output: "Returns whatever values the callable or block emits for each input item.",
                Examples:
                [
                    new("echo one two | foreach func(x) => ($x.ToUpper())", "Iterate with a callable"),
                    new("ls | foreach { _.Name }", "Iterate with a block"),
                ]),
            ["cat"] = new(
                Arguments:
                [
                    new("path ...|-", "One or more file paths to concatenate, or `-` to read piped text input explicitly.", Required: false, TypeName: "path-like|string"),
                ],
                Options:
                [
                    new("-n", "Number every emitted line."),
                    new("-b", "Number only non-blank lines."),
                    new("-s", "Squeeze repeated blank lines into a single blank line."),
                ],
                PipelineInput: new(true, true, true, false, "With no explicit paths, path-like pipeline values are treated as files when they all resolve to existing files; otherwise pipeline values are treated as text input. Use `-` explicitly when you want stdin-style text alongside file arguments."),
                Output: "Returns plain text lines by default, or numbered record rows with `Number` and `Text` when `-n` or `-b` is used."),
            ["wc"] = new(
                Arguments:
                [
                    new("path ...", "Optional file paths to measure. When omitted, `wc` measures the current text pipeline.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-l", "Show line counts."),
                    new("-w", "Show word counts."),
                    new("-c", "Show byte counts."),
                    new("-m", "Show character counts."),
                    new("-L", "Show the longest-line length."),
                ],
                PipelineInput: new(true, true, false, false, "Consumes the current text pipeline when explicit file paths are omitted."),
                Output: "Returns typed text-statistics objects, and appends a `total` row when multiple files are counted."),
            ["touch"] = new(
                Arguments:
                [
                    new("path ...", "One or more filesystem paths to create or timestamp.", TypeName: "path-like"),
                ],
                Options:
                [
                    new("-a", "Update only the access time."),
                    new("-m", "Update only the modification time."),
                    new("-c", "Do not create missing files."),
                    new("-d <time>", "Use an explicit timestamp value."),
                    new("-r <path>", "Copy the timestamp from another file or directory."),
                ],
                PipelineInput: new(false, false, true, false, "Consumes piped path-like input when explicit paths are omitted."),
                Output: "Returns updated FileInfo or DirectoryInfo objects for the paths it touched."),
            ["mv"] = new(
                Arguments:
                [
                    new("source ...", "One or more source paths to move.", TypeName: "path-like"),
                    new("destination", "The destination path or directory.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-n", "Do not overwrite existing files."),
                    new("-u", "Only move when the source is newer than the destination."),
                    new("-f", "Force overwrite for file targets, clearing a previous `-n`."),
                    new("-t <directory>", "Use an explicit destination directory."),
                    new("-T", "Treat the destination as a normal path, not a target directory."),
                ],
                PipelineInput: new(false, false, false, false, "The current `mv` implementation is explicit-arg-first and does not consume pipeline input."),
                Output: "Returns FileInfo or DirectoryInfo objects for the moved targets."),
            ["env"] = new(
                Arguments:
                [
                    new("name ...", "With no assignments, query one or more environment variable names.", Required: false),
                    new("name=value ...", "Temporary environment assignments for `env` output or a nested command.", Required: false),
                    new("command ...", "Optional nested command to run under the temporary environment snapshot.", Required: false),
                ],
                Options:
                [
                    new("-u <name>", "Unset a variable in the temporary environment snapshot."),
                    new("--", "Treat the remaining arguments as the nested command even when there are no assignments."),
                ],
                PipelineInput: new(true, true, false, false, "With no explicit names, piped scalar values are treated as queried environment-variable names."),
                Output: "Returns typed environment-variable entries, or the nested command's output when assignments/unsets are used with a command. Use `$env.NAME` for direct value-only access when you want the variable value instead of the structured `env` entry object. Missing `$env` members resolve to `null` rather than raising a missing-member error.",
                Examples:
                [
                    new("env PATH", "Inspect the structured PATH entry"),
                    new("echo $env.PATH", "Read just the PATH value"),
                    new("echo $env.EDITOR", "Read another environment variable directly"),
                ]),
            ["read-file"] = new(
                Arguments:
                [
                    new("path ...", "One or more file paths to read as whole-text values.", Required: false, TypeName: "path-like"),
                ],
                PipelineInput: new(false, false, true, false, "Consumes piped path-like input when explicit file paths are omitted."),
                Output: "Returns one string value per file."),
            ["read-lines"] = new(
                Arguments:
                [
                    new("path ...", "One or more file paths to read line-by-line.", Required: false, TypeName: "path-like"),
                ],
                PipelineInput: new(false, false, true, false, "Consumes piped path-like input when explicit file paths are omitted."),
                Output: "Returns ShellTextLine values, one per line across the supplied files."),
            ["write-file"] = new(
                Arguments:
                [
                    new("path", "The file path to create or replace.", TypeName: "path-like"),
                    new("value ...", "Optional explicit text values to write. When omitted, pipeline input becomes the file body.", Required: false),
                ],
                PipelineInput: new(true, true, false, false, "When no explicit value arguments are supplied, pipeline values are rendered as plain text and written to the file."),
                Output: "Returns the resulting filesystem entry for the written file."),
            ["append-file"] = new(
                Arguments:
                [
                    new("path", "The file path to append to.", TypeName: "path-like"),
                    new("value ...", "Optional explicit text values to append. When omitted, pipeline input becomes the appended text.", Required: false),
                ],
                PipelineInput: new(true, true, false, false, "When no explicit value arguments are supplied, pipeline values are rendered as plain text and appended to the file."),
                Output: "Returns the resulting filesystem entry for the written file."),
            ["read-bytes"] = new(
                Arguments:
                [
                    new("path ...", "One or more file paths to read as raw byte arrays.", Required: false, TypeName: "path-like"),
                ],
                PipelineInput: new(false, false, true, false, "Consumes piped path-like input when explicit file paths are omitted."),
                Output: "Returns one byte-array value per file."),
            ["write-bytes"] = new(
                Arguments:
                [
                    new("path", "The file path to create or replace.", TypeName: "path-like"),
                    new("bytes ...", "Optional explicit byte-oriented values. When omitted, pipeline input becomes the byte payload.", Required: false),
                ],
                PipelineInput: new(true, true, false, false, "When no explicit byte values are supplied, pipeline input is converted into a byte payload."),
                Output: "Returns the resulting filesystem entry for the written file."),
            ["open-file"] = new(
                Arguments:
                [
                    new("path ...", "One or more file paths to open.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("--read, -r", "Open for reading. This is the default."),
                    new("--write, -w", "Open for writing and replace any previous contents."),
                    new("--append, -a", "Open for writing at the end of the file."),
                    new("--binary, -b", "Open a binary handle instead of a text handle."),
                    new("--encoding <name>", "Use a specific text encoding for text writers."),
                ],
                PipelineInput: new(false, false, true, false, "Consumes piped path-like input when explicit file paths are omitted."),
                Output: "Returns managed file-handle objects that can be passed to `read-from`, `read-line-from`, `read-to-end`, `write-to`, `write-line-to`, `flush`, `close`, `seek`, `position`, `length`, and `copy-to`."),
            ["read-from"] = new(
                Arguments:
                [
                    new("handle", "The managed file handle to read from.", Required: false),
                    new("count", $"Optional chunk size. Defaults to {StreamCommandUtilities.DefaultReadChunkSize}.", Required: false, TypeName: "int"),
                ],
                PipelineInput: new(true, false, false, false, "Consumes a piped file handle when no explicit handle argument is supplied."),
                Output: "Returns a string chunk for text handles or a byte array chunk for binary handles."),
            ["read-line-from"] = new(
                Arguments:
                [
                    new("handle", "The managed text file handle to read from.", Required: false),
                ],
                PipelineInput: new(true, false, false, false, "Consumes a piped file handle when no explicit handle argument is supplied."),
                Output: "Returns the next line as a ShellTextLine value, or nothing at end-of-file."),
            ["read-to-end"] = new(
                Arguments:
                [
                    new("handle", "The managed file handle to read from.", Required: false),
                ],
                PipelineInput: new(true, false, false, false, "Consumes a piped file handle when no explicit handle argument is supplied."),
                Output: "Returns the remaining text or bytes from the handle."),
            ["write-to"] = new(
                Arguments:
                [
                    new("handle", "The managed file handle to write into."),
                    new("value ...", "Optional explicit values to write. When omitted, pipeline input becomes the written payload.", Required: false),
                ],
                PipelineInput: new(true, true, false, false, "When no explicit values are supplied, pipeline values are written into the handle."),
                Output: "Writes into the handle and does not emit pipeline output."),
            ["write-line-to"] = new(
                Arguments:
                [
                    new("handle", "The managed text file handle to write into."),
                    new("value ...", "Optional explicit values to write as one line. When omitted, each pipeline value becomes its own line.", Required: false),
                ],
                PipelineInput: new(true, true, false, false, "When no explicit values are supplied, each pipeline value is written as its own line."),
                Output: "Writes line-oriented text into the handle and does not emit pipeline output."),
            ["flush"] = new(
                Arguments:
                [
                    new("handle ...", "One or more managed file handles to flush.", Required: false),
                ],
                PipelineInput: new(true, false, false, false, "Consumes piped file handles when explicit handles are omitted."),
                Output: "Flushes the handles and does not emit pipeline output."),
            ["close"] = new(
                Arguments:
                [
                    new("handle ...", "One or more managed file handles to close.", Required: false),
                ],
                PipelineInput: new(true, false, false, false, "Consumes piped file handles when explicit handles are omitted."),
                Output: "Closes the handles and does not emit pipeline output."),
            ["seek"] = new(
                Arguments:
                [
                    new("handle", "The managed file handle to reposition.", Required: false),
                    new("offset", "The byte offset to seek to or by.", TypeName: "long"),
                    new("origin", "The seek origin: begin, current, or end. Defaults to begin.", Required: false),
                ],
                PipelineInput: new(true, false, false, false, "Consumes a piped file handle when no explicit handle argument is supplied."),
                Output: "Moves the handle and returns it so you can continue piping into `read-from`, `read-line-from`, `read-to-end`, or `copy-to`."),
            ["position"] = new(
                Arguments:
                [
                    new("handle ...", "One or more managed file handles whose current position should be reported.", Required: false),
                ],
                PipelineInput: new(true, false, false, false, "Consumes piped file handles when explicit handles are omitted."),
                Output: "Returns the current byte position for each supplied handle when that position can be reported safely."),
            ["length"] = new(
                Arguments:
                [
                    new("handle ...", "One or more managed file handles whose current stream length should be reported.", Required: false),
                ],
                PipelineInput: new(true, false, false, false, "Consumes piped file handles when explicit handles are omitted."),
                Output: "Returns the current underlying stream length for each supplied handle."),
            ["copy-to"] = new(
                Arguments:
                [
                    new("source", "The readable managed file handle to copy from.", Required: false),
                    new("target", "The writable managed file handle to copy into."),
                ],
                PipelineInput: new(true, false, false, false, "Consumes a piped source handle when you only pass the target handle explicitly."),
                Output: "Returns the number of bytes or text characters copied into the target handle."),
            ["systemctl"] = new(
                Arguments:
                [
                    new("[list-units [pattern ...]]", "With no subcommand, ToSh treats `systemctl` as a structured `list-units` query. Explicit `list-units` behaves the same way.", Required: false),
                    new("list-unit-files [pattern ...]", "Returns typed unit-file state rows for installed unit files.", Required: false),
                    new("show <unit ...>", "Returns structured systemd unit property sets for one or more units.", Required: false),
                    new("status <unit ...>", "Returns structured unit status objects for one or more units, including recent logs when available.", Required: false),
                    new("<other-subcommand ...>", "Unsupported subcommands fall back to the native `systemctl` utility unchanged.", Required: false),
                ],
                Options:
                [
                    new("--type <type[,type...]>|-t <type[,type...]>", "Restrict structured `list-units` or `list-unit-files` output to specific unit types such as `service` or `socket`."),
                    new("--state <state[,state...]>", "Restrict structured `list-units` output to specific load/active states."),
                    new("--all", "Include inactive and unloaded units in the structured listing."),
                    new("--failed", "Restrict the structured listing to failed units."),
                    new("-p <property[,property...]>|--property <property[,property...]>", "Restrict `show` to specific fetched properties. ToSh still injects `Id` internally so multiple-unit output stays structured."),
                    new("--show <columns>", "Use ToSh display-only column selection on structured unit rows or property sets."),
                    new("--hide <columns>", "Hide display columns while preserving the underlying typed objects."),
                    new("--show-all", "Expose every selectable structured display column for the current output shape."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `systemctl` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns typed systemd unit rows for `list-units`, typed unit-file rows for `list-unit-files`, and structured unit property-set objects for supported `show` and `status` queries. Other subcommands currently fall back to the native `systemctl` output.",
                Examples:
                [
                    new("systemctl", "List units as typed rows"),
                    new("systemctl --type service | where _.Active == active", "Filter active services in the pipeline"),
                    new("systemctl list-unit-files --type service | where _.Enabled", "Inspect installed enabled service unit files"),
                    new("systemctl status sshd.service | get { Id, ActiveState, MainPID, RecentLogCount }", "Inspect structured unit status details"),
                ]),
            ["journalctl"] = new(
                Arguments:
                [
                    new("[query ...]", "Structured journal queries accept common `journalctl` filters such as match expressions, unit filters, priorities, boot selectors, and time windows.", Required: false),
                ],
                Options:
                [
                    new("-n <count>|--lines <count>", "Limit the structured result set to the most recent entries."),
                    new("-u <unit>|--unit <unit>", "Restrict structured output to a specific systemd unit."),
                    new("--since <when>", "Restrict entries to those at or after the supplied time."),
                    new("--until <when>", "Restrict entries to those at or before the supplied time."),
                    new("-p <range>|--priority <range>", "Restrict entries to a priority or priority range."),
                    new("-b [id]|--boot [id]", "Restrict entries to the current boot or a selected boot."),
                    new("-g <pattern>|--grep <pattern>", "Restrict structured output to entries whose messages match a pattern."),
                    new("--user", "Query the user journal instead of the system journal."),
                    new("--system", "Query the system journal explicitly."),
                    new("--show <columns>", "Use ToSh display-only column selection on the structured journal-entry rows."),
                    new("--hide <columns>", "Hide display columns while preserving the underlying structured journal entries."),
                    new("--show-all", "Expose every selectable structured journal-entry display column."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `journalctl` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Streams structured journal-entry objects from `journalctl -o json` for supported non-follow query paths. Unsupported or text-oriented modes fall back to the native utility.",
                Examples:
                [
                    new("journalctl -n 20", "Stream the newest journal entries as structured objects"),
                    new("journalctl -u sshd.service | first 10", "Inspect a unit's logs in the pipeline"),
                    new("journalctl --since yesterday | where _.Priority <= 3", "Filter recent high-priority entries"),
                ]),
            ["loginctl"] = new(
                Arguments:
                [
                    new("[list-sessions|list-users|list-seats]", "With no subcommand, ToSh treats `loginctl` as a structured `list-sessions` query. The explicit list subcommands return typed rows.", Required: false),
                    new("show-session <id ...>|show-user <user ...>|show-seat <seat ...>", "Returns structured login property sets for supported `show-*` queries.", Required: false),
                    new("<other-subcommand ...>", "Unsupported subcommands fall back to the native `loginctl` utility unchanged.", Required: false),
                ],
                Options:
                [
                    new("-p <property[,property...]>|--property <property[,property...]>", "Restrict `show-*` to specific fetched properties. ToSh still injects the relevant identity property internally so multiple-result output stays structured."),
                    new("--all", "Include empty properties in supported `show-*` queries when the underlying `loginctl` invocation supports it."),
                    new("--show <columns>", "Use ToSh display-only column selection on structured list rows or property sets."),
                    new("--hide <columns>", "Hide display columns while preserving the underlying typed objects."),
                    new("--show-all", "Expose every selectable structured display column for the current output shape."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `loginctl` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns typed session, user, or seat rows for supported list queries, and structured property-set objects for supported `show-*` queries. Other subcommands currently fall back to the native `loginctl` output.",
                Examples:
                [
                    new("loginctl", "List sessions as typed rows"),
                    new("loginctl list-users | where _.State == active", "Filter active login users in the pipeline"),
                    new("loginctl show-user 1000 | get { UID, Name, State, Sessions }", "Inspect structured user-login properties"),
                ]),
            ["hostnamectl"] = new(
                Arguments:
                [
                    new("[status]", "With no subcommand, ToSh treats `hostnamectl` as a structured status query. Explicit `status` behaves the same way.", Required: false),
                    new("<other-command ...>", "Mutating or unsupported commands fall back to the native `hostnamectl` utility unchanged.", Required: false),
                ],
                Options:
                [
                    new("--show <columns>", "Use ToSh display-only column selection on the structured host-status object."),
                    new("--hide <columns>", "Hide display columns while preserving the underlying typed host object."),
                    new("--show-all", "Expose every selectable structured host-status display column."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `hostnamectl` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns a structured host-status object for supported JSON-backed `hostnamectl status` queries. Other commands currently fall back to the native `hostnamectl` output.",
                Examples:
                [
                    new("hostnamectl", "Inspect structured host status"),
                    new("hostnamectl --show Hostname,OperatingSystem,KernelRelease", "Render a focused host summary"),
                    new("hostnamectl | get { Hostname, MachineID, BootID }", "Project host identity properties in the pipeline"),
                ]),
            ["networkctl"] = new(
                Arguments:
                [
                    new("[list [pattern ...]]", "With no subcommand, ToSh treats `networkctl` as a structured `list` query. Explicit `list` behaves the same way.", Required: false),
                    new("<other-command ...>", "Unsupported, detail, and mutating commands currently fall back to the native `networkctl` utility unchanged.", Required: false),
                ],
                Options:
                [
                    new("-a|--all", "Pass through to the structured `networkctl list` query to include all visible links."),
                    new("-l|--full", "Pass through to the structured `networkctl list` query."),
                    new("--show <columns>", "Use ToSh display-only column selection on the structured network-link rows."),
                    new("--hide <columns>", "Hide display columns while preserving the underlying typed link objects."),
                    new("--show-all", "Expose every selectable structured network-link display column."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `networkctl` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns typed network-link rows for supported `networkctl list` queries. Other commands currently fall back to the native `networkctl` output.",
                Examples:
                [
                    new("networkctl", "List network links as typed rows"),
                    new("networkctl | where _.Setup == unmanaged", "Filter links by setup state in the pipeline"),
                    new("networkctl --show Link,Operational,Setup,Managed", "Render a focused network-link summary"),
                ]),
            ["http"] = new(
                Arguments:
                [
                    new("<get|post|put|patch|delete|head|options> <url>", "Send an HTTP request immediately.", Required: false),
                    new("request <method> <url>", "Build an immutable HTTP request definition without sending it yet.", Required: false),
                    new("send [request]", "Send an HttpRequestDefinition or HttpRequestMessage from an argument or the pipeline.", Required: false),
                    new("serve|host <dir>", "Start a temporary HTTP file server rooted at a directory and return a live server handle.", Required: false),
                    new("servers", "List open HTTP file server handles.", Required: false),
                    new("stop [handle|id ...]", "Stop one or more HTTP file servers, or all of them when no target is provided.", Required: false),
                ],
                Options:
                [
                    new("--header <name> <value>", "Add a request header. Repeatable."),
                    new("--json <value>", "Serialize a value as JSON request content."),
                    new("--body <text>", "Send plain text request content."),
                    new("--file <path>", "Send request content from a file."),
                    new("--form <record>", "Send application/x-www-form-urlencoded content from a record-like value."),
                    new("--content-type <value>", "Override the request content type."),
                    new("--timeout <duration>", "Set the per-request timeout."),
                    new("--bearer <token>", "Add a Bearer authorization header."),
                    new("--auth basic <user> <pass>", "Add a Basic authorization header."),
                    new("--follow | --no-follow", "Control redirect following for the request."),
                    new("--as <response|json|text|bytes|lines>", "Choose how the response should be materialized."),
                    new("--out <path>", "Write the raw response body bytes to a file."),
                    new("--fail", "Turn HTTP non-success status codes into diagnostics instead of returning a response object/body."),
                    new("--browse", "For `http serve`, render a lightweight browser page with directory listings and share metadata."),
                    new("--upload", "For `http serve`, accept uploads. Browser uploads and raw PUT/POST uploads are both supported."),
                    new("--once", "For `http serve`, close the server after the first handled request."),
                    new("--bind <address>", "Bind `http serve` to a specific address."),
                    new("--lan", "Bind `http serve` to all interfaces and advertise LAN-friendly share URLs for other devices."),
                    new("--port <port>", "Bind `http serve` to a specific port. Use `0` to request an ephemeral port."),
                    new("--index <file>", "Serve this index file for directories before directory listings."),
                    new("--token <token>", "Protect `http serve` with a fixed token. The returned ShareUrl includes it automatically."),
                    new("--generate-token", "Generate a random token for `http serve` and return it on the server handle."),
                ],
                PipelineInput: new(false, true, false, false, "`http send` accepts a single HttpRequestDefinition or HttpRequestMessage from the pipeline."),
                Output: "Returns either a structured HttpResponseInfo object or a decoded body, depending on `--as`. `http serve` returns live HttpFileServerHandle objects.",
                Examples:
                [
                    new("http get https://example.com --as text", "Fetch a text response"),
                    new("http post https://example.com/api --json { Name = \"Toast\" } --as response", "Send JSON and keep the structured response"),
                    new("http request GET https://example.com | http send --as response", "Build then send a request object"),
                    new("http serve ./share --browse", "Start a temporary file server with a lightweight share page"),
                    new("http serve ./share --browse --lan", "Share a directory with other devices on the same network"),
                    new("http serve ./dropbox --upload --generate-token", "Start a temporary upload server with a generated access token"),
                    new("http servers | get { Id, ShareUrl, Upload }", "Inspect open temporary file servers"),
                ]),
            ["ip"] = new(
                Arguments:
                [
                    new("addr|address|a [filter ...]", "Returns typed network-interface objects by invoking `ip -j addr` under the hood.", Required: false),
                    new("link|l [filter ...]", "Returns typed network-interface link objects by invoking `ip -j link`.", Required: false),
                    new("route|r [filter ...]", "Returns typed route objects by invoking `ip -j route`.", Required: false),
                    new("<other-subcommand ...>", "For now, unsupported subcommands fall back to the system `ip` utility unchanged.", Required: false),
                ],
                PipelineInput: new(false, false, false, false, "The structured `ip` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "For `ip addr` and `ip link`, returns typed interface objects with nested typed address objects where available. For `ip route`, returns typed route objects. Other subcommands currently pass through to the system `ip` utility's normal text output.",
                Examples:
                [
                    new("ip addr", "List interfaces as structured objects"),
                    new("ip link | where _.IsUp", "Filter active interfaces in the pipeline"),
                    new("ip route | where _.IsDefault", "Show the default route as a typed object"),
                    new("ip addr | each { _.Addresses } | flatten | get Address", "Project nested typed IP addresses"),
                ]),
            ["lsblk"] = new(
                Arguments:
                [
                    new("[device ...]", "Optional device paths or names to scope the query to.", Required: false, TypeName: "path-like|string"),
                ],
                Options:
                [
                    new("-a", "Include empty devices."),
                    new("-A", "Hide empty devices."),
                    new("-d", "Suppress dependencies and child devices."),
                    new("-b", "Render size-oriented columns in raw bytes."),
                    new("-f", "Use the filesystem-oriented display preset."),
                    new("-m", "Use the permissions-oriented display preset."),
                    new("-t", "Use the topology-oriented display preset."),
                    new("-D", "Use the discard-oriented display preset."),
                    new("-z", "Use the zoned-device display preset."),
                    new("-p", "Show full device paths."),
                    new("-S", "Restrict the query to SCSI devices."),
                    new("-N", "Restrict the query to NVMe devices."),
                    new("-v", "Restrict the query to virtio devices."),
                    new("-I <majors>", "Include only the specified major numbers."),
                    new("-e <majors>", "Exclude the specified major numbers."),
                    new("-x <column>", "Sort by an lsblk column name such as `NAME`, `SIZE`, or `PATH`."),
                    new("-o <columns>", "Select lsblk-style output columns such as `NAME,SIZE,FSTYPE,MOUNTPOINTS`."),
                    new("-O", "Expose every selectable structured lsblk column."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `lsblk` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns reusable block-device objects with nested child devices when the underlying `lsblk --json` output is hierarchical.",
                Examples:
                [
                    new("lsblk", "Browse block devices as a tree-with-columns object table"),
                    new("lsblk -l -f | where _.FsType == \"ntfs\"", "Flatten the block-device view and filter by filesystem type"),
                    new("lsblk -o NAME,PATH,SIZE", "Pick explicit lsblk-style display columns without changing the underlying objects"),
                ]),
            ["lscpu"] = new(
                Arguments:
                [
                    new("[-e|-C]", "Without a mode flag, `lscpu` returns a structured CPU summary. `-e` switches to per-CPU topology rows and `-C` switches to CPU cache rows.", Required: false),
                ],
                Options:
                [
                    new("-B", "Render cache sizes in raw bytes for summary and cache views."),
                    new("-e", "Return per-CPU topology rows from `lscpu --extended --json`."),
                    new("-C", "Return CPU cache rows from `lscpu --caches --json`."),
                    new("-a", "Include both online and offline CPUs in extended mode."),
                    new("-b", "Restrict extended mode to online CPUs."),
                    new("-c", "Restrict extended mode to offline CPUs."),
                    new("-x", "Request hexadecimal CPU masks where the underlying `lscpu` mode supports them."),
                    new("-y", "Request physical IDs instead of logical IDs in extended mode."),
                    new("-o <columns>", "Select lscpu-style columns such as `CPU,NODE,SOCKET,CORE,ONLINE,MHZ` for `-e` or `NAME,LEVEL,TYPE,ONE-SIZE` for `-C`."),
                    new("--output-all", "Expose every selectable structured column for `-e` or `-C`."),
                    new("--hierarchic <when>", "Pass through `auto`, `always`, or `never` for the summary view."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `lscpu` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns a structured CPU summary by default, typed per-CPU topology rows with `-e`, or typed CPU cache rows with `-C`.",
                Examples:
                [
                    new("lscpu", "Show the structured CPU summary"),
                    new("lscpu -e | where _.Online == true | first 8", "Browse structured per-CPU topology rows"),
                    new("lscpu -C -B | get { Name, Level, OneSize, AllSize }", "Inspect cache metadata with byte-oriented sizes"),
                ]),
            ["lsfd"] = new(
                Arguments:
                [
                    new("[-p pid[,pid...]]", "Optionally restrict the query to specific processes.", Required: false),
                ],
                Options:
                [
                    new("-l", "Include thread-level rows with a `TID` column."),
                    new("-p <pid(s)>", "Restrict the query to one or more process ids."),
                    new("-i[4|6]", "Restrict the result set to IPv4 and/or IPv6 sockets."),
                    new("-o <columns>", "Select lsfd-style columns such as `COMMAND,PID,FD,TYPE,NAME`."),
                    new("--summary[=only|append|never]", "Include or isolate lsfd summary counters alongside structured descriptor rows."),
                    new("--show <columns>", "Use ToSh display-only column selection without changing the underlying `FileDescriptorInfo` objects."),
                    new("--hide <columns>", "Hide display columns while keeping the full typed rows in the pipeline."),
                    new("--show-all", "Expose every selectable structured lsfd column discoverable from the local `lsfd -H` catalog."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `lsfd` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns typed open-file-descriptor rows and, when summary mode is enabled, typed counter rows describing totals such as open files and sockets.",
                Examples:
                [
                    new("lsfd", "Browse open file descriptors as typed rows"),
                    new("lsfd -p 1 -o COMMAND,PID,ASSOC,TYPE,NAME", "Inspect a specific process with explicit lsfd columns"),
                    new("lsfd --summary=only", "Show typed summary counters only"),
                ]),
            ["lsipc"] = new(
                Arguments:
                [
                    new("[-m|-M|-q|-Q|-s|-S]", "Select a specific IPC resource family. With no resource flag, `lsipc` returns the global IPC limits and usage summary.", Required: false),
                ],
                Options:
                [
                    new("-m", "Return System V shared-memory rows."),
                    new("-M", "Return POSIX shared-memory rows."),
                    new("-q", "Return System V message-queue rows."),
                    new("-Q", "Return POSIX message-queue rows."),
                    new("-s", "Return System V semaphore rows."),
                    new("-S", "Return POSIX semaphore rows."),
                    new("-g", "Return global usage/limit rows, optionally scoped by `-m`, `-q`, or `-s`."),
                    new("-i <id>", "Restrict the query to a specific System V IPC id."),
                    new("-N <name>", "Restrict the query to a specific POSIX IPC name."),
                    new("-c", "Include creator-related fields such as creator uid, user, and group."),
                    new("-t", "Include time-oriented fields such as attach, detach, change, or last-operation timestamps."),
                    new("-b", "Request byte-oriented numeric sizes from the underlying `lsipc` command."),
                    new("-P", "Render permissions numerically instead of symbolically."),
                    new("-l", "Force list output shape where the underlying `lsipc` mode supports it."),
                    new("-o <columns>", "Select lsipc-style columns such as `KEY,ID,OWNER,SIZE,NATTCH` or `RESOURCE,LIMIT,USED,USE%`."),
                    new("--show <columns>", "Use ToSh display-only column selection on the structured rows after parsing."),
                    new("--hide <columns>", "Hide display columns while keeping the full structured rows in the pipeline."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `lsipc` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns structured IPC resource rows or global IPC limit/usage rows, with typed sizes, counts, and ISO-normalized timestamps where the underlying data supports them.",
                Examples:
                [
                    new("lsipc", "Browse global IPC limits and current usage as structured rows"),
                    new("lsipc -m | first 5", "Inspect System V shared-memory rows"),
                    new("lsipc -g -m | get { Resource, Limit, Used, UsePercent }", "Show the global shared-memory limits and utilization summary"),
                ]),
            ["findmnt"] = new(
                Arguments:
                [
                    new("[path-or-device ...]", "Optional mountpoints or source devices to match.", Required: false, TypeName: "path-like|string"),
                ],
                Options:
                [
                    new("-S <source>", "Match a source device or source specification."),
                    new("-T <target>", "Match the filesystem that contains a target path."),
                    new("-M <mountpoint>", "Match a specific mountpoint."),
                    new("-t <types>", "Limit the result set to specific filesystem types."),
                    new("-O <options>", "Limit the result set to mounts with matching options."),
                    new("-R", "Include submounts for matching filesystems."),
                    new("-U", "Drop duplicate targets."),
                    new("-l", "Return a flattened list instead of a hierarchy."),
                    new("-A", "Disable built-in filters and include everything findmnt normally hides."),
                    new("-b", "Render size-oriented columns in raw bytes."),
                    new("-D", "Use a `df`-style display preset."),
                    new("-I", "Use a `df -i`-style inode display preset."),
                    new("-o <columns>", "Select findmnt-style output columns such as `TARGET,SOURCE,FSTYPE,OPTIONS`."),
                    new("--output-all", "Expose every selectable structured findmnt column."),
                ],
                PipelineInput: new(false, false, false, false, "The structured `findmnt` builtin is explicit-arg-first and does not currently consume pipeline input."),
                Output: "Returns reusable mounted-filesystem objects with nested child mounts when the underlying `findmnt --json` output is hierarchical.",
                Examples:
                [
                    new("findmnt", "Browse the mounted-filesystem tree as typed objects"),
                    new("findmnt -l | where _.Target.StartsWith(\"/run\")", "Flatten the mount tree and filter by target path"),
                    new("findmnt -o TARGET,SOURCE,FSTYPE", "Pick explicit findmnt-style display columns without changing the underlying objects"),
                ]),
            ["summarize"] = new(
                Arguments:
                [
                    new("[column|member-path] [--sum [columns]] [--avg [columns]] [--min [columns]] [--max [columns]] [--count [columns]]", "With no arguments, infer every sensible aggregate for every summarizable column. A single bare column or member path such as `Size` or `_.Used` narrows auto mode to that one target. Flags request explicit operations.", Required: false),
                ],
                Options:
                [
                    new("--sum [columns]", "Compute sums for scalar input or the named columns."),
                    new("--avg [columns], --average [columns]", "Compute averages for scalar input or the named columns."),
                    new("--min [columns]", "Compute minima for scalar input or the named columns."),
                    new("--max [columns]", "Compute maxima for scalar input or the named columns."),
                    new("--count [columns]", "Count input rows when no columns are supplied, or non-null values for the named columns."),
                ],
                PipelineInput: new(true, true, true, true, "Consumes the current pipeline rows and returns one structured ColumnSummary object per requested or inferred scalar target or member path."),
                Output: "Returns ColumnSummary objects describing the requested or inferred aggregates. The original input rows are not appended back into the result.",
                Examples:
                [
                    new("df | summarize", "Infer every sensible aggregate for every summarizable df column"),
                    new("df | summarize _.Used", "Infer every sensible aggregate for a single member path target"),
                    new("seq 5 | summarize --sum --avg --min --max --count", "Summarize a scalar numeric pipeline explicitly"),
                    new("ps | summarize --avg Memory --max Memory", "Compute multiple aggregates over one column"),
                ]),
            ["summary"] = new(
                Arguments:
                [
                    new("[column|member-path] [--sum [columns]] [--avg [columns]] [--min [columns]] [--max [columns]] [--count [columns]]", "Alias for `summarize`.", Required: false),
                ],
                PipelineInput: new(true, true, true, true, "Alias for `summarize`. Consumes the current pipeline rows and returns only ColumnSummary objects."),
                Output: "Alias for `summarize`. Returns ColumnSummary objects only."),
            ["collect"] = new(
                Arguments: [],
                PipelineInput: new(true, true, true, true, "Consumes the current pipeline and buffers every incoming item into one array result."),
                Output: "Returns a single array containing the pipeline items in order.",
                Examples:
                [
                    new("echo 1 2 3 | collect", "Collect scalar pipeline items into one array"),
                    new("ls *.cs | collect", "Capture a multi-item file listing as one value"),
                    new("findmnt -l | where _.FsType == ext4 | collect", "Buffer filtered structured rows into one array"),
                ]),
            ["grep"] = new(
                Arguments:
                [
                    new("pattern|regex", "The text pattern or .NET regular expression to search for.", TypeName: "string|regex"),
                    new("path ...", "Optional file paths to search instead of consuming piped text.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-i", "Ignore case."),
                    new("-m", "Multiline mode."),
                    new("-s", "Singleline mode so `.` matches newlines."),
                    new("-x", "Require a full-line match."),
                    new("--explicit-capture", "Return structured capture results instead of plain matching lines."),
                    new("-v", "Invert the match."),
                    new("-F", "Treat the pattern as a literal string instead of a regex."),
                    new("-n", "Include source line numbers in text-file results."),
                ],
                PipelineInput: new(false, true, false, false, "Consumes scalar text from the pipeline. When paths are supplied explicitly, grep reads file contents instead."),
                Output: "Returns matching text lines by default. When explicit capture output is requested, structured regex capture objects are returned instead.",
                Examples:
                [
                    new("echo one two three | grep tw", "Pipe text into grep"),
                    new("echo \"Alpha\" | grep -i \"^alpha$\"", "Use regex flags"),
                    new("grep -F literal README.md", "Search a file literally"),
                ]),
            ["find"] = new(
                Arguments:
                [
                    new("root ...", "One or more filesystem roots to search.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-name <pattern>", "Filter by shell-style name pattern."),
                    new("-regex <pattern>", "Filter by .NET regex against the root-relative path."),
                    new("-iregex <pattern>", "Case-insensitive regex filter against the root-relative path."),
                    new("-type <file|dir|link>", "Filter by filesystem entry kind."),
                ],
                PipelineInput: new(false, false, true, false, "Uses piped path-like roots when explicit roots are omitted. Falls back to the current directory when neither are present."),
                Output: "Returns typed filesystem entries with rich metadata that flow naturally through the object pipeline."),
            ["ls"] = new(
                Arguments:
                [
                    new("path ...", "Optional directories or files to list.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-a", "Include hidden entries."),
                    new("-A", "Include hidden entries while matching standard almost-all ls behavior."),
                    new("-l", "Use the long listing view."),
                    new("-d", "List directory arguments themselves instead of their contents."),
                    new("-R", "Traverse directories recursively."),
                    new("-F", "Classify names with shell-style suffixes like `*` and `@`."),
                    new("-i", "Include inode metadata in the compact table view."),
                    new("-r", "Reverse the current sort order."),
                    new("-S", "Sort by size descending."),
                    new("-t", "Sort by the active time field descending."),
                    new("--sort <name|size|time>", "Choose the primary listing sort field."),
                    new("--time <modified|access|created>", "Choose which time field long listings and time sorts use."),
                    new("--group-directories-first", "Group directories ahead of files before applying the primary sort."),
                    new("-la", "Combine hidden and long listing output."),
                ],
                PipelineInput: new(false, false, false, false, "Ls is still explicit-arg-first; path input is not yet consumed from the pipeline."),
                Output: "Produces typed filesystem entries that the display layer renders as shell tables by default."),
            ["df"] = new(
                Arguments:
                [
                    new("path ...", "Optional paths used to resolve the containing mounted filesystem.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-h", "Accepts the familiar human-readable flag; ToSh sizes are already typed and human-friendly."),
                    new("-T", "Ensures the filesystem type column is visible."),
                    new("-l", "Restricts the output to local filesystems."),
                    new("-t <type[,type...]>", "Includes only matching filesystem types."),
                    new("-x <type[,type...]>", "Excludes matching filesystem types."),
                    new("--total", "Appends a typed aggregate total row."),
                    new("--output <columns>", "Selects which filesystem properties are rendered."),
                ],
                PipelineInput: new(false, false, true, false, "Uses piped path-like values when explicit paths are omitted. Without path input, `df` lists the full mounted filesystem set."),
                Output: "Produces typed filesystem usage objects, with optional aggregate totals."),
            ["du"] = new(
                Arguments:
                [
                    new("path ...", "Optional roots to measure.", Required: false, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-a", "Include file rows as well as directory summaries."),
                    new("-s", "Summarize each root instead of emitting recursive rows."),
                    new("-d <depth>", "Limit recursion depth."),
                    new("-h", "Accepts the familiar human-readable flag; ToSh sizes are already typed and human-friendly."),
                    new("-c", "Appends a typed grand total row."),
                    new("-x", "Stay on the same filesystem as each requested root."),
                    new("--time", "Include the latest modified timestamp for each emitted row."),
                ],
                PipelineInput: new(false, false, true, false, "Uses piped path-like roots when explicit paths are omitted. Falls back to the current directory when neither are present."),
                Output: "Produces typed path-usage objects with optional modified-time metadata and aggregate totals."),
            ["stat"] = new(
                Arguments:
                [
                    new("path ...", "One or more paths to inspect.", Required: true, TypeName: "path-like"),
                ],
                Options:
                [
                    new("-L", "Dereference symlinks before reading metadata."),
                    new("-f", "Return filesystem usage information for the containing mount instead of file-entry metadata."),
                    new("--show <columns>", "Select which properties are rendered."),
                ],
                PipelineInput: new(false, false, true, false, "Uses piped path-like values when explicit paths are omitted."),
                Output: "Produces typed filesystem-entry metadata by default, or filesystem-usage objects in `-f` mode."),
            ["history"] = new(
                Arguments:
                [
                    new("search <text>", "Searches history entries by text.", Required: false),
                    new("expand <spec>", "Expands a history event specification without running it.", Required: false),
                    new("run <spec>", "Resolves and executes a history event specification.", Required: false),
                    new("delete <spec>", "Deletes one or more history entries by id or spec.", Required: false),
                    new("path|save|reload|clear", "History maintenance subcommands.", Required: false),
                ],
                PipelineInput: new(false, false, false, false, "History is producer-oriented; replay and expansion are explicit subcommands, while `!` syntax remains REPL-only sugar."),
                Output: "Produces structured history entries, file paths, expanded command text, or replay results depending on the chosen subcommand."),
            ["config"] = new(
                Arguments:
                [
                    new("browse [query]", "Opens the full-screen config browser, optionally filtered by an initial query, with staged editing, subtree diffs, structured section and collection editors, reusable confirmation and validation surfaces, filesystem browsing, apply/save flows, startup reload/init actions, live prompt/style/theme previews, and raw text editing for advanced cases.", Required: false),
                    new("get <path>", "Reads one config value.", Required: false),
                    new("set <path> [value]", "Sets one config value.", Required: false),
                    new("reset [section]", "Resets a section or the whole config object.", Required: false),
                    new("reload", "Replays startup config files into the current session.", Required: false),
                    new("init [directory]", "Scaffolds a new config directory.", Required: false),
                ],
                PipelineInput: new(false, true, false, false, "Only `config set` consumes piped scalar input, using it as the new value when no explicit value argument is present."),
                Output: "Produces the live config object, one config value, status rows, or an interactive browser request depending on the form."),
        };

    private static readonly IReadOnlyDictionary<string, LanguageHelpDefinition> ShellTypeTopics =
        new Dictionary<string, LanguageHelpDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["list"] = new(
                Category: "Shell Types",
                Description: "Mutable shell-friendly list type backed by CLR List<T>.",
                Usage: "new list<T>(items...) | new list(items...)",
                Aliases: Array.Empty<string>(),
                Related: ["array", "dict", "set", "table", "tuple", "new", "cast", "constructors", "types"],
                Examples:
                [
                    "var items = new list<String>(\"one\", \"two\")",
                    "var nums = new list(1, 2, 3)",
                    "echo [1, 2, 3] | cast list<int>",
                ],
                Notes: "Use `list<T>` when you want a mutable, single-element-type collection. Omit `<T>` to infer a sensible element type where possible."),
            ["array"] = new(
                Category: "Shell Types",
                Description: "Fixed-size shell array type backed by CLR arrays.",
                Usage: "[item, ...] | new array<T>(items...) | new array(items...)",
                Aliases: Array.Empty<string>(),
                Related: ["list", "set", "tuple", "new", "cast", "constructors", "types"],
                Examples:
                [
                    "var items = [\"one\", \"two\"]",
                    "var nums = new array<int>(1, 2, 3)",
                    "echo [1, 2, 3] | type-of | get Name",
                ],
                Notes: "`[]` is the array literal surface in ToSh. Arrays are fixed-size; use `list<T>` when you want a mutable collection."),
            ["dict"] = new(
                Category: "Shell Types",
                Description: "Shell-friendly string-keyed dictionary type backed by CLR Dictionary<TKey, TValue>.",
                Usage: "new dict<TKey, TValue>(key, value, ...) | new dict(record-like) | new dict(key, value, ...)",
                Aliases: ["map"],
                Related: ["table", "hashtable", "list", "new", "cast", "constructors", "types"],
                Examples:
                [
                    "var meta = new dict<string, int>(One, 1, Two, 2)",
                    "var meta = new map({ Name = \"Toast\", Uid = 1000 })",
                    "echo $meta.Two",
                ],
                Notes: "`dict` is the preferred named map type. `map` is an alias. When key/value types are omitted, ToSh defaults to a string-keyed dictionary and infers values where it can."),
            ["set"] = new(
                Category: "Shell Types",
                Description: "Unique-value shell collection type backed by CLR HashSet<T>.",
                Usage: "new set<T>(items...) | new set(items...)",
                Aliases: Array.Empty<string>(),
                Related: ["list", "array", "dict", "new", "cast", "constructors", "types"],
                Examples:
                [
                    "var tags = new set<string>(food, pantry, pantry)",
                    "var ids = new set(1, 2, 2, 3)",
                ],
                Notes: "Sets enforce uniqueness and infer an element type where possible."),
            ["hashtable"] = new(
                Category: "Shell Types",
                Description: "Hashtable-backed map type for CLR-style hashtable interop.",
                Usage: "new hashtable(record-like) | new hashtable(key, value, ...)",
                Aliases: Array.Empty<string>(),
                Related: ["dict", "table", "new", "constructors", "types"],
                Examples:
                [
                    "var meta = new hashtable(Name, \"Toast\", Uid, 1000)",
                    "echo $meta.Uid",
                ],
                Notes: "Prefer `dict` for most shell code; use `hashtable` when you need CLR hashtable behavior specifically."),
            ["table"] = new(
                Category: "Shell Types",
                Description: "Anonymous dynamic record shape backed by ExpandoObject.",
                Usage: "{ Field = value, ... } | new table(record-like) | new table(key, value, ...)",
                Aliases: ["dynamicrecord"],
                Related: ["record", "dict", "hashtable", "new", "types"],
                Examples:
                [
                    "var person = { Name = \"Toast\", Uid = 1000 }",
                    "var person = new table(Name, \"Toast\", Uid, 1000)",
                    "echo $person.Name",
                ],
                Notes: "`table` is the shell name for anonymous dynamic records. Use named `record` when you want a reusable typed data shape."),
            ["tuple"] = new(
                Category: "Shell Types",
                Description: "Ordered positional shell value backed by the ToSh tuple runtime type.",
                Usage: "new tuple(items...)",
                Aliases: Array.Empty<string>(),
                Related: ["array", "list", "new", "constructors", "types"],
                Examples:
                [
                    "var pair = new tuple(alpha, 42)",
                    "echo $pair.Item2",
                ],
                Notes: "Tuples preserve positional values and expose `Item1`, `Item2`, and so on."),
        };

    public static IReadOnlyList<HelpSummary> BuildSummaries(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        return BuildTopics(runtime)
            .OrderBy(topic => topic.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(topic => topic.Name, StringComparer.OrdinalIgnoreCase)
            .Select(topic => new HelpSummary(
                topic.Name,
                topic.Kind,
                topic.Category,
                topic.Description,
                topic.Usage,
                topic.Aliases))
            .ToArray();
    }

    public static IReadOnlyList<HelpTopic> BuildTopics(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return BuildStaticTopics(runtime);
    }

    public static IReadOnlyList<HelpCategoryInfo> BuildCategories(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        return BuildStaticTopics(runtime)
            .GroupBy(topic => topic.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HelpCategoryInfo(group.Key, group.Count()))
            .ToArray();
    }

    public static HelpTopic? ResolveTopic(ToshRuntime runtime, string name)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var topics = BuildStaticTopicIndex(runtime);

        if (topics.TryGetValue(name, out var topic))
        {
            return topic;
        }

        if (TryResolveShellTopic(runtime, name, out var shellTopic))
        {
            return shellTopic;
        }

        var type = runtime.TypeResolver.Resolve(name);

        if (type is not null)
        {
            return CreateTypeTopic(type);
        }

        var catalogType = TypeCatalog.GetAssemblies()
            .SelectMany(assembly => TypeCatalog.GetAssemblyTypes(assembly))
            .FirstOrDefault(candidate =>
                string.Equals(
                    ReflectionMetadataUtilities.GetDisplayName(candidate),
                    name,
                    StringComparison.OrdinalIgnoreCase));

        if (catalogType is not null)
        {
            return CreateTypeTopic(catalogType);
        }

        var external = ExternalCommandResolver.Resolve(runtime.CurrentDirectory, name);

        return external.Status == ExternalCommandLookupStatus.Found && external.ResolvedPath is not null
            ? CreateExternalTopic(name, external.ResolvedPath)
            : null;
    }

    public static IReadOnlyList<HelpSearchResult> Search(ToshRuntime runtime, string query, int maxResults = 12)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        return BuildStaticTopics(runtime)
            .Select(topic => new { Topic = topic, Score = ScoreTopic(topic, query) })
            .Where(result => result.Score > 0.0d)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Topic.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(result => new HelpSearchResult(
                result.Topic.Name,
                Math.Round(result.Score, 1),
                result.Topic.Kind,
                result.Topic.Category,
                result.Topic.Description,
                result.Topic.Usage,
                result.Topic.Aliases))
            .ToArray();
    }

    public static IReadOnlyList<HelpSearchResult> GetRelated(ToshRuntime runtime, string name, int maxResults = 6)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var topicIndex = BuildStaticTopicIndex(runtime);

        if (!topicIndex.TryGetValue(name, out var topic))
        {
            var resolved = ResolveTopic(runtime, name);

            if (resolved is null)
            {
                return Array.Empty<HelpSearchResult>();
            }

            return resolved.Related
                .Where(topicIndex.ContainsKey)
                .Take(maxResults)
                .Select((relatedName, index) => ToSearchResult(topicIndex[relatedName], 100.0d - (index * 5.0d)))
                .ToArray();
        }

        return topic.Related
            .Where(topicIndex.ContainsKey)
            .Take(maxResults)
            .Select((relatedName, index) => ToSearchResult(topicIndex[relatedName], 100.0d - (index * 5.0d)))
            .ToArray();
    }

    private static Dictionary<string, HelpTopic> BuildStaticTopicIndex(ToshRuntime runtime)
    {
        var index = new Dictionary<string, HelpTopic>(StringComparer.OrdinalIgnoreCase);

        foreach (var topic in BuildStaticTopics(runtime))
        {
            index.TryAdd(topic.Name, topic);

            foreach (var alias in topic.Aliases)
            {
                index.TryAdd(alias, topic);
            }
        }

        return index;
    }

    private static IReadOnlyList<HelpTopic> BuildStaticTopics(ToshRuntime runtime)
    {
        var commands = runtime.Commands.All.ToArray();
        var aliasMap = BuildBuiltInAliasMap(commands);
        var topics = new List<HelpTopic>(commands.Length + LanguageTopics.Count);

        foreach (var command in commands)
        {
            var kind = ToHelpSubjectKind(command);
            aliasMap.TryGetValue(command.Name, out var aliases);
            CommandDetailsByName.TryGetValue(command.Name, out var details);
            topics.Add(new HelpTopic(
                Name: command.Name,
                Kind: kind,
                Category: DetermineCommandCategory(command.Name, kind),
                Description: command.Description,
                Usage: command.Usage,
                Aliases: aliases ?? Array.Empty<string>(),
                Related: Array.Empty<string>(),
                Examples: ExamplesByName.TryGetValue(command.Name, out var examples) ? examples : Array.Empty<string>(),
                Path: null,
                Notes: GetCommandNotes(command.Name),
                Arguments: details?.Arguments,
                Options: details?.Options,
                PipelineInput: details?.PipelineInput,
                Output: details?.Output,
                ExampleItems: details?.Examples));
        }

        foreach (var (name, definition) in LanguageTopics)
        {
            topics.RemoveAll(topic => string.Equals(topic.Name, name, StringComparison.OrdinalIgnoreCase));
            topics.Add(new HelpTopic(
                Name: name,
                Kind: HelpSubjectKind.Language,
                Category: definition.Category,
                Description: definition.Description,
                Usage: definition.Usage,
                Aliases: definition.Aliases,
                Related: definition.Related,
                Examples: definition.Examples,
                Path: null,
                Notes: definition.Notes));
        }

        foreach (var (name, definition) in ShellTypeTopics)
        {
            topics.RemoveAll(topic => string.Equals(topic.Name, name, StringComparison.OrdinalIgnoreCase));
            topics.Add(new HelpTopic(
                Name: name,
                Kind: HelpSubjectKind.Type,
                Category: definition.Category,
                Description: definition.Description,
                Usage: definition.Usage,
                Aliases: definition.Aliases,
                Related: definition.Related,
                Examples: definition.Examples,
                Path: "ToSh",
                Notes: definition.Notes));
        }

        return AddRelatedTopics(topics);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildBuiltInAliasMap(IEnumerable<IShellCommand> commands)
    {
        var groups = commands
            .Where(command => ToHelpSubjectKind(command) == HelpSubjectKind.BuiltIn)
            .GroupBy(BuildAliasKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();

        var aliasMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var names = group
                .Select(command => command.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var name in names)
            {
                aliasMap[name] = names
                    .Where(candidate => !string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        return aliasMap;
    }

    private static string BuildAliasKey(IShellCommand command)
    {
        return string.Join(
            "|",
            command.GetType().FullName,
            command.Description,
            NormalizeUsage(command));
    }

    private static string NormalizeUsage(IShellCommand command)
    {
        return command.Usage.StartsWith(command.Name, StringComparison.OrdinalIgnoreCase)
            ? "<name>" + command.Usage[command.Name.Length..]
            : command.Usage;
    }

    private static IReadOnlyList<HelpTopic> AddRelatedTopics(IReadOnlyList<HelpTopic> topics)
    {
        return topics
            .Select(topic => topic with
            {
                Related = topic.Related
                    .Concat(
                        topics
                    .Where(candidate => !string.Equals(candidate.Name, topic.Name, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => !topic.Aliases.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
                    .Where(candidate => !candidate.Aliases.Contains(topic.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(candidate => new { candidate.Name, Score = ScoreRelated(topic, candidate) })
                    .Where(result => result.Score > 0.0d)
                    .OrderByDescending(result => result.Score)
                    .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(result => result.Name)
                    .Take(8))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToArray(),
            })
            .ToArray();
    }

    private static double ScoreTopic(HelpTopic topic, string query)
    {
        var trimmed = query.Trim();

        if (trimmed.Length == 0)
        {
            return 0.0d;
        }

        double score = 0.0d;

        if (string.Equals(topic.Name, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 120.0d);
        }

        if (topic.Aliases.Any(alias => string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 116.0d);
        }

        if (topic.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 96.0d);
        }

        if (topic.Aliases.Any(alias => alias.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 90.0d);
        }

        if (topic.Category.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 24.0d;
        }

        if (topic.Description.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 34.0d;
        }

        if (topic.Usage.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 28.0d;
        }

        if (!string.IsNullOrWhiteSpace(topic.Notes) &&
            topic.Notes.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 18.0d;
        }

        var fuzzyNameScore = HelpFuzzyMatcher.FuzzyMatch(topic.Name, trimmed);
        var fuzzyAliasScore = topic.Aliases.Select(alias => HelpFuzzyMatcher.FuzzyMatch(alias, trimmed)).DefaultIfEmpty(0.0d).Max();

        if (fuzzyNameScore >= 0.55d)
        {
            score = Math.Max(score, fuzzyNameScore * 88.0d);
        }

        if (fuzzyAliasScore >= 0.55d)
        {
            score = Math.Max(score, fuzzyAliasScore * 80.0d);
        }

        var queryTokens = ExtractTokens(trimmed);
        var topicTokens = ExtractTokens(topic.Name, topic.Category, topic.Description, topic.Usage, topic.Notes ?? string.Empty, string.Join(' ', topic.Aliases));
        score += queryTokens.Count(token => topicTokens.Contains(token)) * 6.0d;

        return score;
    }

    private static double ScoreRelated(HelpTopic left, HelpTopic right)
    {
        double score = 0.0d;

        if (string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase))
        {
            score += 40.0d;
        }

        if (left.Kind == right.Kind)
        {
            score += 8.0d;
        }

        var leftTokens = ExtractTokens(left.Name, left.Description, left.Usage, left.Notes ?? string.Empty);
        var rightTokens = ExtractTokens(right.Name, right.Description, right.Usage, right.Notes ?? string.Empty);
        score += leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count() * 8.0d;

        return score;
    }

    private static HelpSearchResult ToSearchResult(HelpTopic topic, double score)
    {
        return new HelpSearchResult(
            topic.Name,
            Math.Round(score, 1),
            topic.Kind,
            topic.Category,
            topic.Description,
            topic.Usage,
            topic.Aliases);
    }

    private static HelpSubjectKind ToHelpSubjectKind(IShellCommand command)
    {
        return command is ICommandResolutionMetadata metadata
            ? metadata.ResolutionKind switch
            {
                CommandResolutionKind.BuiltIn => HelpSubjectKind.BuiltIn,
                CommandResolutionKind.Alias => HelpSubjectKind.Alias,
                CommandResolutionKind.Function => HelpSubjectKind.Function,
                CommandResolutionKind.External => HelpSubjectKind.External,
                _ => HelpSubjectKind.BuiltIn,
            }
            : HelpSubjectKind.BuiltIn;
    }

    internal static string DetermineCommandCategory(string name, HelpSubjectKind kind)
    {
        if (kind is HelpSubjectKind.Alias or HelpSubjectKind.Function)
        {
            return "Scripting";
        }

        return name switch
        {
            "help" or "apropos" or "history" or "history-search" or "view" or "clear" or "exit" or "which" or "whence"
                => "Shell",
            "write" or "writeline" or "echo" or "head" or "tail" or "wc" or "uniq" or "cut" or "tr" or "grep" or "split" or "join-lines" or "replace" or "match" or "template"
                => "Text",
            "pwd" or "cd" or "ls" or "df" or "mounts" or "du" or "usage" or "disk-usage" or "stat" or "find" or "cat" or "read-file" or "read-lines" or "write-file" or "append-file" or "read-bytes" or "write-bytes" or "open-file" or "read-from" or "read-line-from" or "read-to-end" or "write-to" or "write-line-to" or "flush" or "close" or "seek" or "position" or "length" or "copy-to" or "mkdir" or "touch" or "rm" or "cp" or "mv" or "chmod" or "chown" or "ln" or "readlink" or "realpath" or "dirname" or "basename" or "exists" or "is-file" or "is-dir" or "is-link" or "mkdir-temp" or "tempfile"
                => "Filesystem",
            "ps" or "jobs" or "wait-for" or "kill" or "signal" => "Process",
            "ping" or "ip" or "http" => "Network",
            "lsblk" => "Filesystem",
            "findmnt" => "Filesystem",
            "lscpu" => "System",
            "lsfd" => "Process",
            "lsipc" => "System",
            "uname" or "hostname" or "whoami" or "id" or "env" or "free" or "uptime" or "sleep" or "seq" or "date" or "timespan" or "export" or "forget" or "unset"
                => "System",
            "get" or "rename" or "inspect" or "where" or "each" or "first" or "last" or "skip" or "sort" or "sort-by" or "reverse" or "count" or "flatten" or "distinct" or "group-by" or "take-while" or "skip-while" or "tee" or "sum" or "average" or "avg" or "min" or "max" or "summarize" or "summary" or "xargs"
                => "Pipeline",
            "styled" or "prompt-time" or "prompt-dir" or "prompt-git" or "prompt-userhost" or "prompt-history" or "prompt-jobs" or "prompt-duration" or "prompt-exit" or "prompt-text" or "prompt-newline"
                => "Prompt",
            "from" or "to" or "parse" or "hash" or "as-file"
                => "Data",
            "type-of" or "describe-type" or "members" or "methods" or "constructors" or "types" or "load-assembly" or "cast" or "new" or "call"
                or "has-prop" or "has-method" or "get-props" or "get-methods"
                or "get-prop" or "set-prop" or "del-prop" or "call-method" or "clone"
                => "CLR",
            _ => "Shell",
        };
    }

    internal static string? GetCommandNotes(string name)
    {
        return name switch
        {
            "help" => "Use `help search <query>` or `apropos <query>` to find commands and language topics quickly, `help --cli` for the inline fuzzy tree browser, or `help browse` for the fullscreen split-pane browser. In the REPL, `F1` opens the inline help browser seeded from the token under the cursor, and `Alt+H` is available as a fallback on terminals that do not expose function keys cleanly.",
            "apropos" => "Apropos performs fuzzy help search across commands and Tosh language topics.",
            "history" => "History is file-backed in normal interactive sessions and each entry now has a stable id. Use `history path`, `history search <text>`, `history delete <spec>`, `history save`, `history reload`, `history clear`, `history expand 237`, or `history run 237` to inspect or replay it. In the REPL, `Ctrl+R`, `!!`, `!237`, `!-2`, `!prefix`, `!?text?`, `!$`, `!^`, `!*`, and `^old^new^` also work as interactive history features.",
            "where" => "Inside predicate expressions, bare member access resolves against the current pipeline object.",
            "each" => "Collections stay intact until you explicitly expand them with `each` or `flatten`.",
            "inspect" => "Inspect opens an inline tree browser for CLR values in interactive sessions. Use `-a` for non-public/static members, `--flat` for the legacy static inspection object output, and `i` inside the browser to insert the selected member text into the active REPL line at the cursor (or queue it for the next prompt when no line is active). In the REPL, `F2` tries to inspect the reference under the cursor, and `Alt+I` is available as a fallback on terminals that do not expose function keys cleanly.",
            "parse" => "Parse and match use .NET regular expressions, including named groups and inline modifiers like `(?im)`.",
            "from" or "to"
                => "The `from` and `to` commands convert between text formats (json, csv, tsv, xml, toml) and CLR objects. Parsed values stay as CLR objects until you explicitly flatten them.",
            "as-file" => "As-file materializes pipeline values into a temporary file and returns a file object you can pass to external executables.",
            "jobs" => "Jobs lists ToSh background jobs started with a trailing `&`. A background launch updates `$tosh.Last.Result` with the started job info, while `jobs` and `wait-for` are the primary inspection commands.",
            "exec" => "Exec replaces the current ToSh process with an external command. On Unix-like systems it uses native process replacement, so `exec tosh` or `exec zsh` behaves like the shell built-in you may know from zsh.",
            "cat" => "With no explicit paths, `cat` treats piped values as file paths only when every value resolves to an existing file. Otherwise it treats the pipeline as text input. Use `-` explicitly when mixing file paths with piped text.",
            "wc" => "Wc returns typed statistics objects instead of formatted text, so you can still `get`, `where`, or `summarize` them later. Selector flags like `-l` and `-w` only change the visible columns, not the underlying objects.",
            "touch" => "Touch now supports `-a`, `-m`, `-c`, `-d`, and `-r`, plus grouped short flags like `-am`.",
            "mv" => "Mv now overwrites existing file targets by default, closer to Unix `mv`. `-n`, `-u`, `-t`, and `-T` are available to control that behavior explicitly.",
            "env" => "Env keeps its object-returning query mode, but it can also build a temporary environment snapshot with `name=value` and `-u name`, optionally running a nested command under that snapshot.",
            "http" => "The native `http` builtin is object-first and backed by .NET HttpClient. Use `--as response` for a structured response object, or `--as json|text|bytes|lines` to project the body directly. `http serve <dir>` starts a temporary file server and returns a live server handle; `--browse`, `--upload`, `--token`, `--generate-token`, and `--lan` turn it into a lightweight cross-platform sharing tool. Use `http servers`, `http stop`, or `close` to manage it.",
            "ip" => "ToSh now wraps `ip addr`, `ip link`, and `ip route` around the JSON-capable system utility so the result flows through the pipeline as typed interface, address, and route objects. Other subcommands still fall back to the external `ip` utility unchanged.",
            "lsblk" => "ToSh wraps `lsblk --json --bytes --output-all` so block devices stay as typed objects in the pipeline while the default renderer can still show them as a tree with columns. The default result is hierarchical, so use `-l` when you want flat filtering in a pipeline, and shell-facing aliases like `FsType`, `FsVer`, and `FsAvail` match the visible column names. Output-format-only flags like `--pairs`, `--raw`, and `--noheadings` currently fall back to the external `lsblk` utility unchanged.",
            "lscpu" => "ToSh wraps the JSON-capable `lscpu` modes instead of scraping text output. The default command yields a structured CPU summary, `-e` yields per-CPU topology rows, and `-C` yields cache rows. `--parse`, raw-only, help, version, and sysroot modes currently fall back to the external `lscpu` utility unchanged.",
            "lsfd" => "ToSh wraps `lsfd --json` so open-file-descriptor rows stay typed in the pipeline. `--summary=only` yields typed counters, and `--summary=append` returns both row and summary objects. Text-format-only modes like `--raw`, `--noheadings`, filter expressions, and custom counters currently fall back to the external `lsfd` utility unchanged.",
            "lsipc" => "ToSh wraps `lsipc -J` so IPC resources and global IPC limits flow through the pipeline as structured rows instead of terminal-shaped text. Text-format-only modes like `--raw`, `--export`, `--newline`, and shell-variable output currently fall back to the external `lsipc` utility unchanged.",
            "findmnt" => "ToSh wraps `findmnt --json --bytes --output-all` so mounted filesystems stay as typed objects in the pipeline while the default renderer can still show them as a tree with columns. The default result is hierarchical, so use `-l` when you want flat filtering in a pipeline, and shell-facing aliases like `FsType` and `FsRoot` match the visible column names. Output-format-only modes like `--pairs`, `--raw`, `--noheadings`, polling, and verification currently fall back to the external `findmnt` utility unchanged.",
            "wait-for" => "Wait-for blocks until one or more background jobs finish and returns structured completion objects.",
            "kill" => "Kill can stop either a ToSh background job or a native operating-system process by pid.",
            "signal" => "Signal sends a named or numeric signal to a ToSh job or a native process id.",
            "ps" => "Process memory values are surfaced as Tosh StorageSize objects, not raw strings.",
            "ls" => "Filesystem metadata stays typed in the pipeline, even when Tosh renders it like a shell table.",
            "read-file" or "read-lines" or "read-bytes" => "These commands accept normal path-like values, including strings, FileInfo, and ToSh FileSystemEntry objects.",
            "write-file" or "append-file" => "These commands use ToSh's plain-text serialization rules, not the rich table renderer, so they are safe for intentional file output.",
            "write-bytes" => "Write-bytes accepts raw byte arrays, byte-like collections, and byte-convertible scalar values. Strings are encoded as UTF-8 in this first slice.",
            "open-file" => "Open-file is the start of ToSh's managed stream system. It returns explicit text or binary handle objects instead of hiding file resources behind implicit properties. Active handles are also visible through `$tosh.Session.OpenHandles` and `$tosh.Session.OpenHandleCount`.",
            "read-from" or "read-line-from" or "read-to-end" or "write-to" or "write-line-to" or "flush" or "close" or "seek" or "position" or "length" or "copy-to" => "These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.",
            "new" => "Tosh supports both the legacy `new <Type> ...` command form and the newer C#-style `new Type(...)` expression syntax. Shell collection types also support generic construction like `new list<String>(...)`.",
            "types" => "Types searches both CLR types and ToSh shell types like `list`, `array`, `dict`, `table`, and `tuple`.",
            "constructors" => "Constructors works with CLR types, ToSh named types, and shell collection types like `list<int>` and `dict<string, int>`.",
            "cast" => "Cast converts to CLR target types, including constructed generic collection types like `list<int>`.",
            _ => null,
        };
    }

    private static bool TryResolveShellTopic(ToshRuntime runtime, string name, out HelpTopic topic)
    {
        if (runtime.Classes.TryGetValue(name, out var rawDescriptor) &&
            rawDescriptor is IShellTypeDescriptor descriptor)
        {
            var aliases = runtime.Classes
                .Where(entry => entry.Value is IShellTypeDescriptor candidate &&
                                string.Equals(candidate.ShellFullName, descriptor.ShellFullName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .Where(alias => !string.Equals(alias, descriptor.ShellTypeName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            topic = CreateShellTypeTopic(descriptor, aliases);
            return true;
        }

        if (BuiltInShellTypes.TryResolveStaticType(name, runtime.TypeResolver, out var builtInType) &&
            builtInType is IShellTypeDescriptor builtInDescriptor)
        {
            topic = CreateShellTypeTopic(builtInDescriptor);
            return true;
        }

        topic = null!;
        return false;
    }

    private static HelpTopic CreateShellTypeTopic(IShellTypeDescriptor descriptor, IReadOnlyList<string>? aliases = null)
    {
        var constructorCount = descriptor.GetShellConstructors().Count;
        var memberCount = descriptor.GetShellMembers().Count(member => !member.IsStatic && !member.IsHidden);
        var methodCount = descriptor.GetShellMethods().Count(method => !method.IsStatic && !method.IsHidden);
        var category = string.Equals(descriptor.ShellNamespace, "ToSh", StringComparison.OrdinalIgnoreCase)
            ? "Shell Types"
            : "ToSh";
        var kindLabel = descriptor.ShellIsEnum
            ? "enum"
            : descriptor.ShellIsValueType
                ? "value type"
                : descriptor.ShellIsClass
                    ? "type"
                    : "type";
        var usage = descriptor.GetShellConstructors().FirstOrDefault()?.Signature ?? $"new {descriptor.ShellTypeName}(...)";
        var examples = ExamplesByName.TryGetValue(descriptor.ShellTypeName, out var predefined)
            ? predefined
            : new[]
            {
                $"describe-type {descriptor.ShellTypeName}",
                $"constructors {descriptor.ShellTypeName}",
            };

        return new HelpTopic(
            Name: descriptor.ShellTypeName,
            Kind: HelpSubjectKind.Type,
            Category: category,
            Description: $"ToSh {kindLabel} {descriptor.ShellFullName}.",
            Usage: usage,
            Aliases: aliases ?? Array.Empty<string>(),
            Related: ["describe-type", "members", "methods", "constructors", "types", "new"],
            Examples: examples,
            Path: descriptor.ShellAssemblyName,
            Notes: $"Namespace: {descriptor.ShellNamespace ?? "<global>"} | Base: {descriptor.ShellBaseTypeName ?? "System.Object"} | Members: {memberCount} | Methods: {methodCount} | Constructors: {constructorCount}");
    }

    private static HelpTopic CreateTypeTopic(Type type)
    {
        var typeName = ReflectionMetadataUtilities.GetDisplayName(type);
        var constructors = type.GetConstructors().Length;
        var methods = type.GetMethods().Length;
        var members = type.GetMembers().Length;

        return new HelpTopic(
            Name: type.Name,
            Kind: HelpSubjectKind.Type,
            Category: "CLR",
            Description: $"CLR type {typeName} from assembly {type.Assembly.GetName().Name}.",
            Usage: $"new {typeName}(...)",
            Aliases: Array.Empty<string>(),
            Related: ["describe-type", "members", "methods", "constructors", "cast", "new"],
            Examples:
            [
                $"describe-type {typeName}",
                $"methods {typeName} | first 10",
            ],
            Path: type.Assembly.GetName().Name,
            Notes: $"Namespace: {type.Namespace ?? "<global>"} | Base: {ReflectionMetadataUtilities.GetDisplayName(type.BaseType ?? typeof(object))} | Members: {members} | Methods: {methods} | Constructors: {constructors}");
    }

    private static HelpTopic CreateExternalTopic(string name, string path)
    {
        return new HelpTopic(
            Name: name,
            Kind: HelpSubjectKind.External,
            Category: "External",
            Description: "External executable resolved from PATH or an explicit executable path.",
            Usage: $"{name} [args ...]",
            Aliases: Array.Empty<string>(),
            Related: ["which", "parse", "from", "to", "xargs"],
            Examples: Array.Empty<string>(),
            Path: path,
            Notes: "External stdout displays as plain text by default and can be adapted into objects with parse/from-* commands.");
    }

    private static HashSet<string> ExtractTokens(params string[] values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                    continue;
                }

                FlushToken(builder, tokens);
            }

            FlushToken(builder, tokens);
        }

        return tokens;
    }

    private static void FlushToken(StringBuilder builder, ISet<string> tokens)
    {
        if (builder.Length <= 1)
        {
            builder.Clear();
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
    }

    private sealed record LanguageHelpDefinition(
        string Category,
        string Description,
        string Usage,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> Related,
        IReadOnlyList<string> Examples,
        string? Notes);

    internal sealed record HelpDetailDefinition(
        IReadOnlyList<HelpArgumentInfo>? Arguments = null,
        IReadOnlyList<HelpOptionInfo>? Options = null,
        HelpPipelineInputInfo? PipelineInput = null,
        string? Output = null,
        IReadOnlyList<HelpExample>? Examples = null);
}

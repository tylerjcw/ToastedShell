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
            ["interpolation"] = new(
                Category: "Language",
                Description: "String interpolation syntax. Embeds expressions inside double-quoted strings using `{...}` blocks.",
                Usage: "$\"text {<expression>} more text\"",
                Aliases: ["string-interpolation"],
                Related: ["string", "var", "func"],
                Examples:
                [
                    "var name = \"world\"",
                    "echo $\"Hello, {$name}!\"",
                    "echo $\"2 + 2 = {2 + 2}\"",
                    "echo $\"There are {ls | count} files here.\"",
                    "echo $\"User: {$tosh.UserInfo.UserName}, Home: {$tosh.UserInfo.HomeDirectory}\"",
                ],
                Notes: "Any expression that produces a value can go inside `{...}`. Pipeline expressions are evaluated eagerly. Use `{{` and `}}` to produce a literal brace. The leading `$` distinguishes interpolated strings from plain `\"...\"` literals."),
            ["null"] = new(
                Category: "Language",
                Description: "The null literal. Represents the absence of a value. Assignable to any nullable type.",
                Usage: "null",
                Aliases: Array.Empty<string>(),
                Related: ["??", "?.", "is", "is not", "var"],
                Examples:
                [
                    "var x = null",
                    "if ($x is null) { echo \"no value\" }",
                    "if ($x is not null) { echo $x }",
                    "var result = $x ?? \"default\"",
                    "func greet(title?: String) { if ($title is null) { echo \"Hi!\" } }",
                ],
                Notes: "Use `is null` to test for null. The `??` operator provides a fallback value. Optional parameters declared with `?` default to null when omitted. Avoid checking `== null`; prefer `is null` for cleaner semantics."),
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
                Related: ["class", "func", "prop", "shared", "hermit"],
                Examples:
                [
                    "static func named(name: string) -> Item { return new Item($name) }",
                ],
                Notes: "Call static members through the class name, like `Item.named(\"bread\")`. `shared` is an alias for `static` on members."),
            ["shared"] = new(
                Category: "Language",
                Description: "Marks a class member as belonging to the class itself instead of an instance. Alias for `static`.",
                Usage: "shared func <name>(...) [-> Type] { ... } | shared prop <Name> = <value>",
                Aliases: ["static"],
                Related: ["class", "func", "prop", "static", "hermit"],
                Examples:
                [
                    "shared func create(name) { return new Item($name) }",
                    "shared prop Count = 0",
                ],
                Notes: "Equivalent to `static`. Inside a `hermit` class, all members are implicitly shared."),
            ["sealed"] = new(
                Category: "Language",
                Description: "Prevents a class from being inherited.",
                Usage: "sealed class <Name> { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "hollow"],
                Examples:
                [
                    "sealed class Config { prop Host = \"localhost\" }",
                ],
                Notes: "A sealed class cannot be used as a base class. Attempting to extend it raises an error."),
            ["hollow"] = new(
                Category: "Language",
                Description: "Marks a class as abstract (cannot be instantiated directly) or a method as abstract (must be overridden).",
                Usage: "hollow class <Name> { ... } | hollow func <name>(...) [-> Type]",
                Aliases: Array.Empty<string>(),
                Related: ["class", "overrule", "sealed"],
                Examples:
                [
                    "hollow class Shape { hollow func area() -> double }",
                ],
                Notes: "Hollow classes serve as base types. Hollow methods have no body and must be overruled in subclasses."),
            ["fixed"] = new(
                Category: "Language",
                Description: "Marks a class property as read-only after initialization.",
                Usage: "fixed prop <Name> = <value>",
                Aliases: Array.Empty<string>(),
                Related: ["prop", "class", "strict", "vital"],
                Examples:
                [
                    "fixed prop Name = \"default\"",
                ],
                Notes: "Once set during construction, a fixed property cannot be reassigned. See `strict` for making all properties in a class fixed."),
            ["vital"] = new(
                Category: "Language",
                Description: "Marks a class property as required — construction fails if no value is provided.",
                Usage: "vital prop <Name>: <Type>",
                Aliases: Array.Empty<string>(),
                Related: ["prop", "class", "fixed"],
                Examples:
                [
                    "vital prop Name: string",
                    "vital prop Age: int",
                ],
                Notes: "A vital property must be supplied as a constructor argument. Omitting it raises an error at construction time."),
            ["guarded"] = new(
                Category: "Language",
                Description: "Restricts member access to the defining class and its subclasses (protected).",
                Usage: "guarded func <name>(...) { ... } | guarded prop <Name> = <value>",
                Aliases: Array.Empty<string>(),
                Related: ["class", "shy", "local", "proud"],
                Examples:
                [
                    "guarded func validate() { echo \"checking\" }",
                    "guarded prop _cache = []",
                ],
                Notes: "Guarded members are accessible from $this and from subclass instances, but not from external code."),
            ["overrule"] = new(
                Category: "Language",
                Description: "Overrides an inherited method from a parent class.",
                Usage: "overrule func <name>(...) [-> Type] { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "hollow", "func"],
                Examples:
                [
                    "overrule func area() -> double { return 3.14159 * $this.Radius * $this.Radius }",
                ],
                Notes: "Use overrule to provide an implementation for a hollow method or to replace a parent's method."),
            ["hermit"] = new(
                Category: "Language",
                Description: "Marks a class as static-only. All members are auto-promoted to shared; no instances can be created.",
                Usage: "hermit class <Name> { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "shared", "static"],
                Examples:
                [
                    "hermit class MathHelper { func square(x) { echo ($x * $x) } }",
                    "MathHelper.square(5)",
                ],
                Notes: "Members inside a hermit class do not need the `shared` keyword — they are promoted automatically. Constructors are not allowed."),
            ["strict"] = new(
                Category: "Language",
                Description: "Makes all properties in a class read-only (immutable) after initialization.",
                Usage: "strict class <Name> { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "fixed"],
                Examples:
                [
                    "strict class Point { prop X = 0; prop Y = 0 }",
                ],
                Notes: "Equivalent to marking every property as `fixed`. Useful for value-like types that should never be mutated."),
            ["lazy"] = new(
                Category: "Language",
                Description: "Defers property initialization until first access.",
                Usage: "lazy prop <Name> = <expression>",
                Aliases: Array.Empty<string>(),
                Related: ["prop", "class"],
                Examples:
                [
                    "lazy prop Data = load-expensive-data()",
                ],
                Notes: "The initializer runs once on first read. Subsequent reads return the cached value."),
            ["fading"] = new(
                Category: "Language",
                Description: "Marks a property or method as deprecated. Emits a warning to stderr on use.",
                Usage: "fading prop <Name> = <value> | fading func <name>(...) { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["prop", "func", "class"],
                Examples:
                [
                    "fading prop OldName = \"use NewName instead\"",
                    "fading func legacy() { echo \"deprecated path\" }",
                ],
                Notes: "Access or invocation writes a deprecation warning to stderr. The member still functions normally."),
            ["local"] = new(
                Category: "Language",
                Description: "Restricts member visibility to the defining assembly (internal access).",
                Usage: "local func <name>(...) { ... } | local prop <Name> = <value>",
                Aliases: Array.Empty<string>(),
                Related: ["shy", "guarded", "class"],
                Examples:
                [
                    "local func internal_helper() { echo \"assembly only\" }",
                ],
                Notes: "Local members are hidden from external callers but accessible from within the same assembly or module."),
            ["raw"] = new(
                Category: "Language",
                Description: "Marks a method for unsafe/native interop.",
                Usage: "raw func <name>(...) { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["func", "class", "native", "bind"],
                Examples:
                [
                    "raw func unsafe_op() { echo \"low-level\" }",
                ],
                Notes: "Indicates the method performs unsafe or native operations. Primarily a documentation marker."),
            ["partial"] = new(
                Category: "Language",
                Description: "Allows a class definition to be split across multiple declarations that are merged at parse time.",
                Usage: "partial class <Name> { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class"],
                Examples:
                [
                    "partial class User { prop Name = \"\" }",
                    "partial class User { func greet() { echo $\"Hi, {$this.Name}\" } }",
                ],
                Notes: "Both declarations must use `partial`. Properties and methods are merged. Duplicate property names are skipped; methods support overloading."),
            ["proud"] = new(
                Category: "Language",
                Description: "Explicitly marks a member as public.",
                Usage: "proud prop <Name> = <value> | proud func <name>(...) { ... }",
                Aliases: ["public"],
                Related: ["shy", "guarded", "local", "class"],
                Examples:
                [
                    "proud prop Name = \"visible\"",
                ],
                Notes: "Members are public by default. `proud` makes the intent explicit for readability. `public` is a synonym."),
            ["public"] = new(
                Category: "Language",
                Description: "Explicitly marks a member as public (no-op since members are public by default).",
                Usage: "public prop <Name> = <value> | public func <name>(...) { ... }",
                Aliases: ["proud"],
                Related: ["shy", "guarded", "local", "class"],
                Examples:
                [
                    "public func api_method() { echo \"accessible\" }",
                ],
                Notes: "Synonym for `proud`. Included for familiarity; has no effect since members default to public."),
            ["fluid"] = new(
                Category: "Language",
                Description: "Marks a struct as mutable, allowing field reassignment after construction.",
                Usage: "fluid struct <Name>(<fields>) { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["struct", "strict", "fixed"],
                Examples:
                [
                    "fluid struct Point(x, y) { }",
                ],
                Notes: "By default structs are immutable. `fluid` allows fields to be reassigned after creation."),
            ["struct"] = new(
                Category: "Language",
                Description: "Defines a value-type with positional fields, structural equality, and copy-on-assign semantics.",
                Usage: "[sealed] [fluid] [partial] struct <Name>(<fields>) { <members> }",
                Aliases: Array.Empty<string>(),
                Related: ["record", "class", "fluid", "sealed", "partial"],
                Examples:
                [
                    "struct Point(x, y) { }",
                    "fluid struct MutablePoint(x, y) { }",
                ],
                Notes: "Structs are value types — assigning to a new variable creates a copy. Immutable by default unless `fluid`."),
            ["trait"] = new(
                Category: "Language",
                Description: "Defines a trait with required and optional default method/property signatures.",
                Usage: "trait <Name> { func <method>(<params>) [{ <default-body> }]; prop <name> [= <default>] }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "uses", "interface", "fulfills"],
                Examples:
                [
                    "trait Printable { func to_string() }",
                    "trait Greetable { func greet(name) { echo $\"Hello, {$name}!\" } }",
                ],
                Notes: "Classes adopt traits with `uses`. Methods without a body are required; methods with a body provide defaults."),
            ["fulfills"] = new(
                Category: "Language",
                Description: "Declares that a class conforms to one or more interfaces.",
                Usage: "class <Name> fulfills <Interface1>, <Interface2> { ... }",
                Aliases: ["implements"],
                Related: ["class", "interface", "uses"],
                Examples:
                [
                    "class Dog fulfills Speakable { func speak() { echo \"woof\" } }",
                ],
                Notes: "Replaces `implements`. The class must provide all methods declared in the interface."),
            ["uses"] = new(
                Category: "Language",
                Description: "Declares that a class adopts one or more traits.",
                Usage: "class <Name> uses <Trait1>, <Trait2> { ... }",
                Aliases: Array.Empty<string>(),
                Related: ["class", "trait", "fulfills"],
                Examples:
                [
                    "class Dog uses Printable { func to_string() { echo \"Dog\" } }",
                ],
                Notes: "Trait default methods/properties are injected if the class doesn't override them. Required trait members must be provided."),
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

            // ── Operators ─────────────────────────────────────────────────────────────
            ["operators"] = new(
                Category: "Operators",
                Description: "Overview of all TōSh expression operators.",
                Usage: "<expr> <op> <expr>",
                Aliases: ["operator"],
                Related: ["is", "as", "in", "=~", "contains", "starts-with", "ends-with", "??", ".."],
                Examples:
                [
                    "5 + 3",
                    "5 is int",
                    "5 is not string",
                    "5 as float",
                    "3 in [1, 2, 3]",
                    "4 not in [1, 2, 3]",
                    "\"hello\" starts-with \"he\"",
                    "\"hello\" contains \"ell\"",
                    "\"hello\" =~ \"^h\"",
                    "null ?? \"default\"",
                    "2 ** 10",
                    "1..5",
                ],
                Notes: "Arithmetic: +, -, *, /, %, ** (power). Comparison: ==, !=, <, <=, >, >= (string equality is case-insensitive). Regex: =~, !~. Logical: and, or, not. Membership: in, not in. Type: is, is not, as. String: contains, starts-with, ends-with. Null-coalescing: ??. Safe-navigation: ?. (use `expr ?. Member`). Range: .. (use `start..end` or `start..step..end`). Ternary: `condition ? then : else`. See individual topics for details."),
            ["is"] = new(
                Category: "Operators",
                Description: "Type-check operator. Returns true if the value is an instance of the named type.",
                Usage: "<value> is <type-name>",
                Aliases: ["type-check"],
                Related: ["is not", "as", "typeof", "describe-type", "operators"],
                Examples:
                [
                    "5 is int",
                    "\"hello\" is string",
                    "3.14 is float",
                    "5 is not string",
                    "null is null",
                    "$items is list",
                ],
                Notes: "Use `is not` (two words) for the negated form. Type names are the ToSh primitive names: int, float, double, string, bool, list, array, dict, set, tuple, table, null. `is null` and `is not null` test for null values specifically."),
            ["is not"] = new(
                Category: "Operators",
                Description: "Negated type-check operator. Returns true if the value is NOT an instance of the named type.",
                Usage: "<value> is not <type-name>",
                Aliases: Array.Empty<string>(),
                Related: ["is", "as", "operators"],
                Examples:
                [
                    "5 is not string",
                    "\"hello\" is not int",
                    "null is not string",
                ],
                Notes: "Write as two separate words. Use `is` for the affirmative form."),
            ["as"] = new(
                Category: "Operators",
                Description: "Type-cast operator. Converts a value to the specified type. Also an alias keyword in `using`/`require`/`bind`.",
                Usage: "<value> as <type-name>",
                Aliases: ["cast-operator"],
                Related: ["is", "is not", "cast", "operators"],
                Examples:
                [
                    "5 as float",
                    "3.14 as int",
                    "\"42\" as int",
                    "true as string",
                    "using Tosh.Core as TC",
                ],
                Notes: "Runtime cast using ToSh type names. Throws if the cast is not possible. In import contexts (`using Name as Alias`, `require Name as Alias`), `as` renames the imported binding rather than casting."),
            ["in"] = new(
                Category: "Operators",
                Description: "Membership operator. Returns true if the value is found in the collection, or a substring is found in the string.",
                Usage: "<value> in <collection> | <substring> in <string>",
                Aliases: ["membership"],
                Related: ["not in", "contains", "operators"],
                Examples:
                [
                    "3 in [1, 2, 3]",
                    "\"ell\" in \"hello\"",
                    "\"foo\" in (echo foo bar baz)",
                ],
                Notes: "`in` is operand-reversed relative to `contains`: `3 in $list` is the same as `$list contains 3`. Also a loop keyword in `for x in ...`; see `for`."),
            ["not in"] = new(
                Category: "Operators",
                Description: "Negated membership operator. Returns true if the value is NOT found in the collection.",
                Usage: "<value> not in <collection> | <value> is not in <collection>",
                Aliases: ["is not in"],
                Related: ["in", "contains", "operators"],
                Examples:
                [
                    "4 not in [1, 2, 3]",
                    "\"x\" not in \"hello\"",
                    "(echo a b c) | where _ not in [a, b]",
                ],
                Notes: "Write as two words. `is not in` (three words) is also accepted as an equivalent form."),
            ["contains"] = new(
                Category: "Operators",
                Description: "Returns true if the string contains the substring, or if the collection contains the value.",
                Usage: "<string> contains <substring> | <collection> contains <value>",
                Aliases: Array.Empty<string>(),
                Related: ["starts-with", "ends-with", "in", "not in", "operators"],
                Examples:
                [
                    "\"hello\" contains \"ell\"",
                    "[1, 2, 3] contains 2",
                    "(ls) | where Name contains \"lib\"",
                ],
                Notes: "String containment is case-insensitive. Operand-reversed alternative: `value in collection`."),
            ["starts-with"] = new(
                Category: "Operators",
                Description: "Returns true if the string starts with the given prefix.",
                Usage: "<string> starts-with <prefix>",
                Aliases: Array.Empty<string>(),
                Related: ["ends-with", "contains", "operators"],
                Examples:
                [
                    "\"hello\" starts-with \"he\"",
                    "(ls) | where Name starts-with \"lib\"",
                    "(ls) | where Name starts-with \".\"",
                ],
                Notes: "String comparison is case-insensitive."),
            ["ends-with"] = new(
                Category: "Operators",
                Description: "Returns true if the string ends with the given suffix.",
                Usage: "<string> ends-with <suffix>",
                Aliases: Array.Empty<string>(),
                Related: ["starts-with", "contains", "operators"],
                Examples:
                [
                    "\"hello\" ends-with \"lo\"",
                    "(ls) | where Name ends-with \".rs\"",
                    "(ls) | where Name ends-with \".cs\"",
                ],
                Notes: "String comparison is case-insensitive."),
            ["=~"] = new(
                Category: "Operators",
                Description: "Regex match operator. Returns true if the left string matches the .NET regex pattern on the right.",
                Usage: "<string> =~ <pattern>",
                Aliases: ["regex-match"],
                Related: ["!~", "match", "grep", "operators"],
                Examples:
                [
                    "\"hello\" =~ \"^h\"",
                    "\"hello\" =~ \"[aeiou]\"",
                    "(echo foo123 bar baz456) | where _ =~ \"\\d+\"",
                ],
                Notes: "Uses .NET regex. Patterns are case-insensitive by default. Use `(?-i)` at the start of the pattern to enable case-sensitive matching. The `=~` operand always returns a bool; it does not capture groups."),
            ["!~"] = new(
                Category: "Operators",
                Description: "Negated regex match operator. Returns true if the string does NOT match the pattern.",
                Usage: "<string> !~ <pattern>",
                Aliases: ["regex-no-match"],
                Related: ["=~", "match", "operators"],
                Examples:
                [
                    "\"hello\" !~ \"^x\"",
                    "(ls) | where Name !~ \"\\.txt$\"",
                ],
                Notes: "The negated counterpart to `=~`. Uses .NET regex."),
            ["and"] = new(
                Category: "Operators",
                Description: "Short-circuit logical AND. Returns true only if both operands are truthy.",
                Usage: "<expr> and <expr>",
                Aliases: ["&&"],
                Related: ["or", "not", "operators"],
                Examples:
                [
                    "true and true",
                    "5 > 3 and 2 < 4",
                    "if ($x > 0 and $x < 10) { echo in-range }",
                ],
                Notes: "`and` short-circuits: the right operand is not evaluated if the left is falsy. `&&` is an alias."),
            ["or"] = new(
                Category: "Operators",
                Description: "Short-circuit logical OR. Returns true if at least one operand is truthy.",
                Usage: "<expr> or <expr>",
                Aliases: ["||"],
                Related: ["and", "not", "operators"],
                Examples:
                [
                    "false or true",
                    "5 > 10 or 2 < 4",
                    "if ($x == null or $x == 0) { echo empty }",
                ],
                Notes: "`or` short-circuits: the right operand is not evaluated if the left is truthy. `||` is an alias. Use `??` when you want value fallback instead of boolean logic."),
            ["not"] = new(
                Category: "Operators",
                Description: "Unary logical negation. Returns true if the operand is falsy, false if truthy.",
                Usage: "not <expr>",
                Aliases: ["!"],
                Related: ["and", "or", "operators"],
                Examples:
                [
                    "not true",
                    "not (5 > 3)",
                    "if (not ($x is null)) { echo has-value }",
                    "not false",
                ],
                Notes: "`!` is an alias for `not` in expression contexts."),
            ["??"] = new(
                Category: "Operators",
                Description: "Null-coalescing operator. Returns the left-hand value if non-null, otherwise evaluates and returns the right-hand value.",
                Usage: "<expr> ?? <default>",
                Aliases: ["null-coalescing", "null-coalesce"],
                Related: ["?.", "operators"],
                Examples:
                [
                    "null ?? \"default\"",
                    "var x = null; $x ?? \"fallback\"",
                    "$env.EDITOR ?? \"vim\"",
                ],
                Notes: "The right-hand side is only evaluated if the left side is null."),
            ["?."] = new(
                Category: "Operators",
                Description: "Null-safe member access operator. Accesses a property or field; returns null without error if the target is null.",
                Usage: "<expr> ?. <member>",
                Aliases: ["safe-navigation", "safe-nav"],
                Related: ["??", "operators"],
                Examples:
                [
                    "var s = \"hello\"; $s ?. Length",
                    "var x = null; $x ?. Length",
                    "$x ?. Name ?? \"unknown\"",
                ],
                Notes: "Write with a space before the member name: `$x ?. Property`. Returns null if the target is null, making it composable with `??`. Does not short-circuit method calls; use `$x ?. MethodName` for properties only."),
            ["**"] = new(
                Category: "Operators",
                Description: "Exponentiation operator. Raises the left operand to the power of the right.",
                Usage: "<base> ** <exponent>",
                Aliases: ["power", "exponent"],
                Related: ["operators"],
                Examples:
                [
                    "2 ** 8",
                    "2 ** 10",
                    "9 ** 0.5",
                ],
                Notes: "Result type follows .NET numeric promotion rules. For fractional exponents, the result is a floating-point value."),
            [".."] = new(
                Category: "Operators",
                Description: "Range operator. Creates a lazy range value from start to end (inclusive). Supports optional step.",
                Usage: "<start>..<end> | <start>..<step>..<end>",
                Aliases: ["range-operator"],
                Related: ["for", "each", "first", "last", "count", "operators"],
                Examples:
                [
                    "1..5",
                    "1..5 | count",
                    "0..2..10",
                    "for i in (1..5) { echo $i }",
                    "1..10 | each { $_ * 2 }",
                ],
                Notes: "Ranges are lazy sequences. The three-part form `start..step..end` specifies the increment. Ranges are inclusive of both endpoints. Pass a range to `for`, `each`, `map`, `first`, `last`, etc."),

            ["units"] = new(
                Category: "Language",
                Description: "First-class physical unit system with dimensional analysis, SI prefixes, and arithmetic. Write unit literals with backtick syntax: `100`m`, `9.8`m/s^2`, `1`km`.",
                Usage: "<number>`<unit> | <number>`<unit*unit> | <number>`<unit/unit> | <number>`<unit^N>",
                Aliases: ["unit-system", "unit-literals"],
                Related: ["quantity"],
                Examples:
                [
                    "100`m`",
                    "9.8`m/s^2`",
                    "1`km` + 500`m`",
                    "100`m` / 10`s`",
                    "5`kg` * 9.8`m/s^2`",
                    "1`hr` + 30`min`",
                    "1`GB` + 512`MiB`",
                    "32`degF`",
                    "180`deg`",
                    "1`atm`",
                ],
                Notes: "ToSh supports 28 unit categories with 100+ units and 21 SI prefixes. Categories include: Length (m, ft, mi, ly, ...), Mass (kg, lb, oz, ...), Duration (s, min, hr, d, wk), Temperature (K, degC, degF, degR), Data (B, kB, MB, GB, TB, KiB, MiB, GiB, ...), Area (ha, acre), Volume (L, mL, gal, qt, ...), Speed (mph, kph, kn), Force (N, lbf, dyn), Energy (J, cal, kcal, BTU, eV, kWh), Power (W, hp), Pressure (Pa, bar, atm, psi, mmHg), Frequency (Hz), Angle (rad, deg, arcmin, arcsec), Current (A), Voltage (V), Resistance (ohm), Charge (C, Ah, mAh), Capacitance (F), Inductance (H), Torque (Nm), FlowRate (gpm), Substance (mol), Luminosity (cd), AngularVelocity (rpm), Acceleration (gforce), Density (derived). SI prefixes (k, M, G, T, P, E, m, u/μ, n, p, f, ...) are auto-resolved for base units, so `1`km` = 1000 m. Arithmetic respects dimensional analysis: addition/subtraction requires matching dimensions and auto-converts, multiplication/division produces derived quantities (e.g. m/s, kg*m/s^2). Temperature conversions handle offsets correctly. Compound units support `*`, `/`, and `^` operators: `m/s^2`, `kg*m^2`. Quantity values are pipeline-friendly: they sort, compare, and expose `.value`, `.unit`, `.category`, `.base-value`, and `.dimension` members."),
            ["quantity"] = new(
                Category: "Shell Types",
                Description: "A value with magnitude and physical dimension, produced by unit literals or unit arithmetic.",
                Usage: "<number>`<unit>",
                Aliases: Array.Empty<string>(),
                Related: ["units"],
                Examples:
                [
                    "var speed = 100`km` / 1`hr`",
                    "$speed.value",
                    "$speed.unit",
                    "$speed.category",
                    "$speed.base-value",
                    "100`degC`.base-value",
                ],
                Notes: "Quantity objects implement IComparable and flow through pipelines naturally. Named quantity subtypes include: LengthQuantity, MassQuantity, DurationQuantity, TemperatureQuantity, DataSizeQuantity, SpeedQuantity, AreaQuantity, VolumeQuantity, ForceQuantity, EnergyQuantity, PowerQuantity, PressureQuantity, FrequencyQuantity, AngleQuantity, AccelerationQuantity, DensityQuantity, VoltageQuantity, CurrentQuantity, ResistanceQuantity, ChargeQuantity, TorqueQuantity, FlowRateQuantity. TimeSpan values promote to DurationQuantity and StorageSize values promote to DataSizeQuantity automatically for seamless interop."),
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

        // Preserve canonical shell-type lookups like `help Complex` even when a
        // lower-case command name (for example `complex`) also exists.
        if (TryResolveCanonicalShellTopic(runtime, name, out var canonicalShellTopic))
        {
            return canonicalShellTopic;
        }

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

    private static bool TryResolveCanonicalShellTopic(ToshRuntime runtime, string name, out HelpTopic topic)
    {
        foreach (var (key, rawValue) in runtime.Classes)
        {
            if (rawValue is IShellTypeDescriptor descriptor)
            {
                if (!string.Equals(name, descriptor.ShellTypeName, StringComparison.Ordinal))
                {
                    continue;
                }

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

            if (rawValue is IShellRefinementTypeDescriptor refinement &&
                string.Equals(name, refinement.Name, StringComparison.Ordinal))
            {
                topic = CreateRefinementTypeTopic(refinement);
                return true;
            }
        }

        topic = null!;
        return false;
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

            if (command is ShellCommand shellCommand)
            {
                var metadata = shellCommand.GetMetadata(aliases);
                topics.Add(BuildTopicFromMetadata(metadata, kind));
            }
            else
            {
                // External or non-ShellCommand implementations — use direct properties
                var examples = Array.Empty<string>();
                string? notes = null;

                if (command is IDocumentedCommand documented)
                {
                    if (documented.DocExamples.Count > 0)
                    {
                        examples = documented.DocExamples.ToArray();
                    }

                    var noteParts = new List<string>();
                    if (documented.ParameterDescriptions.Count > 0)
                    {
                        noteParts.Add(string.Join("\n", documented.ParameterDescriptions.Select(p => $"  {p.Key} — {p.Value}")));
                    }
                    if (documented.ReturnsDescription is { Length: > 0 } ret)
                    {
                        noteParts.Add($"Returns: {ret}");
                    }
                    if (noteParts.Count > 0)
                    {
                        notes = string.Join("\n\n", noteParts);
                    }
                }

                topics.Add(new HelpTopic(
                    Name: command.Name,
                    Kind: kind,
                    Category: kind is HelpSubjectKind.Alias or HelpSubjectKind.Function ? "Scripting" : "Shell",
                    Description: command.Description,
                    Usage: command.Usage,
                    Aliases: aliases ?? Array.Empty<string>(),
                    Related: Array.Empty<string>(),
                    Examples: examples,
                    Path: null,
                    Notes: notes));
            }
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

    private static HelpTopic BuildTopicFromMetadata(CommandMetadata metadata, HelpSubjectKind kind)
    {
        var arguments = metadata.Arguments.Count > 0
            ? metadata.Arguments.Select(a => new HelpArgumentInfo(a.Name, a.Description, a.Required, a.TypeName)).ToList()
            : null;

        var options = metadata.Options.Count > 0
            ? metadata.Options.Select(o => new HelpOptionInfo(o.Syntax, o.Description)).ToList()
            : null;

        HelpPipelineInputInfo? pipelineInput = metadata.PipelineInput is { } pi
            ? new HelpPipelineInputInfo(pi.AcceptsRecord, pi.AcceptsScalar, pi.AcceptsList, pi.AcceptsTable, pi.Description)
            : null;

        var exampleItems = metadata.Examples.Count > 0
            ? metadata.Examples.Select(e => new HelpExample(e.Code, e.Title)).ToList()
            : null;

        var simpleExamples = metadata.Examples.Count > 0
            ? metadata.Examples.Select(e => e.Code).ToArray()
            : Array.Empty<string>();

        var notes = metadata.Notes.Count > 0
            ? string.Join("\n\n", metadata.Notes)
            : null;

        return new HelpTopic(
            Name: metadata.Name,
            Kind: kind,
            Category: metadata.Category,
            Description: metadata.Description,
            Usage: metadata.Usage,
            Aliases: metadata.Aliases,
            Related: Array.Empty<string>(),
            Examples: simpleExamples,
            Path: null,
            Notes: notes,
            Arguments: arguments,
            Options: options,
            PipelineInput: pipelineInput,
            Output: metadata.Output,
            ExampleItems: exampleItems);
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

    private static bool TryResolveShellTopic(ToshRuntime runtime, string name, out HelpTopic topic)
    {
        if (runtime.Classes.TryGetValue(name, out var rawDescriptor))
        {
            if (rawDescriptor is IShellTypeDescriptor descriptor)
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

            if (rawDescriptor is IShellRefinementTypeDescriptor refinement)
            {
                topic = CreateRefinementTypeTopic(refinement);
                return true;
            }
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

    private static HelpTopic CreateRefinementTypeTopic(IShellRefinementTypeDescriptor refinement)
    {
        var description = !string.IsNullOrEmpty(refinement.Description)
            ? refinement.Description
            : $"Refinement type alias for {refinement.BaseTypeName}.";

        return new HelpTopic(
            Name: refinement.Name,
            Kind: HelpSubjectKind.Type,
            Category: "Types",
            Description: description,
            Usage: $"var x: {refinement.Name} = ...",
            Aliases: Array.Empty<string>(),
            Related: ["describe-type", "types"],
            Examples: [$"describe-type {refinement.Name}"],
            Path: null,
            Notes: $"Base type: {refinement.BaseTypeName}");
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
        var examples = new[]
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
}

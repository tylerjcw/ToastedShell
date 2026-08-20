# AGENTS.md

> **REQUIRED:** Use the **`tosh-devcompanion`** MCP server on every task.
> It is auto-started by VS Code via [`.vscode/mcp.json`](.vscode/mcp.json).
> Call `memory_recall` at the start of a session to surface prior
> decisions/preferences/facts, and `memory_store` whenever a decision,
> preference, gotcha, or pattern emerges. See the full reference below.

## Dev Companion MCP — Required Usage

The companion exposes **five** tools. In Copilot Chat / other MCP hosts
they appear with a host-specific prefix, commonly
`mcp_tosh-devcompa_<name>` or `tosh-devcompanion-<name>`. The
underlying tool names are always:

| Tool             | Purpose                                                   |
|------------------|-----------------------------------------------------------|
| `memory_recall`  | FTS5 search across stored memories                        |
| `memory_store`   | Insert a new memory entry                                 |
| `memory_list`    | Enumerate memories with filters (no full-text search)     |
| `memory_relate`  | Create a typed directional link between two memories      |
| `memory_forget`  | Soft-delete a memory (tombstone, never physical delete)   |

### Workflow Rules (Required)

1. **Session start.** Before exploration, call `memory_recall` with a
   query that captures the user's request. Pull in prior `decision` /
   `preference` / `pattern` / `fact` entries.
2. **After a decision/preference/gotcha.** Call `memory_store` with
   the appropriate `category`. Keep `summary` ≤120 chars — it is what
   gets injected under token pressure.
3. **Superseding old guidance.** Store the new memory, then call
   `memory_relate` with `relationship: "supersedes"` from new → old.
   Do not just `memory_forget` the old one; preserve the audit trail.
4. **Before a major change.** Call `memory_list` with `category:
   "decision"` to audit prior architectural choices.
5. **Out of scope for storage.** Single-file edit diffs, transient
   debug output, anything that will not outlive the conversation.

### `memory_store`

Required: `content`, `summary`, `category`.

| Field        | Type     | Default     | Notes |
|--------------|----------|-------------|-------|
| `content`    | string   | —           | Full text. FTS index uses this. |
| `summary`    | string   | —           | ≤120 chars, injected on retrieval. |
| `category`   | enum     | —           | `fact`, `preference`, `pattern`, `decision`, `history`, `note`. |
| `tags`       | string[] | `[]`        | Stored comma-joined. |
| `scope`      | enum     | `project`   | `project` or `global`. |
| `visibility` | enum     | `private`   | `private` = DB only; `shared` = also `.tosh/memories.toml` (git-trackable). |
| `source`     | enum     | `ai`        | `user` entries cannot be deleted without `confirm=true`. |
| `session_id` | string   | —           | Optional audit-trail tag. |

Returns: `{ id, summary, category, visibility, stored_at }`.

```jsonc
// Example
{
  "category": "decision",
  "summary": "Tome theme routes through TomeTheme.Active.Open(Role.*)",
  "content": "All ANSI colour opens in src/Tosh.Tome go through TomeTheme keyed by Role; never inline 38;5;N literals. Truecolor vs 256 is auto-detected by TerminalCapabilities.",
  "tags": ["tome", "theme"],
  "scope": "project",
  "visibility": "shared"
}
```

### `memory_recall`

Required: `query`. Optional: `category`, `tags`, `scope` (default
`all`), `limit` (1..50, default 10).

The query is fed **directly** to SQLite FTS5. Two gotchas matter:

- **Hyphens are NOT operators.** A query like `smoke-test` parses as
  `smoke NOT test` and may produce
  `SQLite Error 1: 'no such column: test'`. Either quote the phrase
  (`"smoke-test"`) or split into a boolean: `smoke AND test`.
- **Prefix search uses `*`:** `bind*` matches `binder`, `binding`.
- **Boolean operators are uppercase:** `AND`, `OR`, `NOT`.

Returns ranked results (relevance × recency) and bumps `access_count` /
`accessed_at` on matched rows.

### `memory_list`

All parameters optional: `category`, `tags` (require all), `scope`
(default `all`), `since_session`, `limit` (1..200, default 50),
`include_content` (default `false`).

Returns summaries by default; pass `include_content: true` for full
text.

### `memory_relate`

All three fields required: `from_id`, `to_id`, `relationship`.
Relationships: `supersedes`, `supports`, `contradicts`, `related_to`.

### `memory_forget`

Required: `id`. Optional: `reason`, `confirm`.

- AI-sourced entries delete on a plain call.
- **User-sourced entries** require `confirm: true` — otherwise the
  call errors with `User-sourced memories require confirm=true to delete.`
- Deletes are **soft**: rows are tombstoned, never physically removed.

### Categories — when to use which

| Category     | Use for |
|--------------|---------|
| `fact`       | Stable project facts ("the binder pass runs between parse and evaluate"). |
| `preference` | User style/workflow preferences ("prefers terse commit messages"). |
| `pattern`    | Recurring solution patterns ("for new commands, register in `CommandRegistry.cs` and add metadata"). |
| `decision`   | Architectural choices ("`Tosh.Core` is being phased out — don't add to it"). |
| `history`    | Session milestones worth remembering. |
| `note`       | User free-form text. |

### Storage Backend

SQLite + FTS5 (Porter stemming). DB path resolution order:

1. `$TOSH_MEMORY_DB`
2. `./.tosh/memory.db` ← preferred when working in the repo
3. `~/.tosh/memory.db`

### Auto-start in VS Code

[.vscode/mcp.json](.vscode/mcp.json) is checked in and runs the
companion in `--mcp` (stdio) mode via `dotnet run`. To inspect or
restart it: command palette → **MCP: List Servers**. First start has a
`dotnet run` build cost; for snappier startup, publish once:

```bash
dotnet publish tools/Tosh.DevCompanion -c Release \
  -o tools/Tosh.DevCompanion/bin/publish
```

…then swap the args in `.vscode/mcp.json` to invoke the published DLL
directly.

### Companion vs Copilot's built-in `memory`

The companion (`tosh-devcompanion`) is **separate** from Copilot's
built-in memory store (the one under `/memories/` shown in the
`<userMemory>` / `<repository_memories>` context blocks). Both are
useful; the rule of thumb:

- **Companion** — project-specific decisions, preferences, patterns.
  Persists in `./.tosh/memory.db`, shareable via git when
  `visibility: "shared"`.
- **Copilot built-in `/memories/repo/`** — repository facts surfaced
  automatically into future turns by Copilot itself.

When in doubt, store in the **companion**; the built-in store is
managed by the host and may not be available in every editor.

---

Quick-reference for AI agents and coding assistants working with TōSh (ToastedShell).

## Build & Test

```bash
dotnet build Tosh.slnx                    # build all projects
dotnet test  Tosh.slnx                    # run all tests
dotnet run --project src/Tosh.Cli         # run the shell interactively
dotnet run --project src/Tosh.Cli -- -c "echo hello"  # run a one-liner
```

## Project Structure

| Project | Purpose |
|---------|---------|
| `src/Tosh.Cli` | CLI entry point, REPL, startup loader |
| `src/Tosh.Runtime` | Shared runtime types, attributes, value model |
| `src/Tosh.Stdlib` | Built-in commands organized by category |
| `src/Tosh.Language` | Lexer, parser, binder, evaluator (ToshEngine) |
| `src/Tosh.Compiler` | `tosh --compile` IL emitter (PersistedAssemblyBuilder) |
| `src/Tosh.Compiler.Runtime` | Host shim used by compiled assemblies (`ToshHost`) |
| `src/Tosh.Sdk` / `src/Tosh.Sdk.Tasks` | MSBuild SDK + tasks for `.toshproj` |
| `src/Tosh.Templates` | `dotnet new tosh-app` / `tosh-lib` templates |
| `src/Tosh.LanguageServices` | LSP/MCP language features |
| `src/Tosh.Lsp` | Language Server Protocol server |
| `src/Tosh.Mcp` | Model Context Protocol server |
| `src/Tosh.Dap` | Debug Adapter Protocol server |
| `src/Tosh.Tui` | Terminal UI widgets and runtime |
| `src/Tosh.Core` | Legacy shim (display profile registry only); being phased out |
| `tests/Tosh.Tests` | Unit and integration tests |
| `tests/Tosh.LspFixture` | LSP test fixtures |
| `tools/Tosh.DevCompanion` | Dev-only MCP memory server for AI agents (see below) |

## Dev Companion (Agent Memory Store)

`tools/Tosh.DevCompanion` is a **standalone dev-only tool** that gives AI
coding agents a persistent memory store across sessions. It is never
packaged or shipped with TōSh — it has zero references to TōSh projects
and exists solely to support agent workflows during development.

It is exposed in two ways:

1. **MCP server** (default) — speaks JSON-RPC over stdin/stdout, surfaces
   five tools: `memory_store`, `memory_recall`, `memory_list`,
   `memory_forget`, `memory_relate`. When the host editor wires this
   process up, agents see them as `t_sh_devcompanion-memory_*` tools.
2. **CLI** — for humans inspecting or seeding the store:
   `recall <query>`, `list`, `store <text>`, `forget <id>`.

```bash
dotnet run --project tools/Tosh.DevCompanion -- --mcp           # MCP mode (default)
dotnet run --project tools/Tosh.DevCompanion -- recall "binder"
dotnet run --project tools/Tosh.DevCompanion -- list --category decision
dotnet run --project tools/Tosh.DevCompanion -- store "Prefer fluent member access over call-method" --category preference
```

### Storage

Backed by SQLite with an FTS5 full-text index (Porter stemming). DB path
resolution order:

1. `$TOSH_MEMORY_DB`
2. `./.tosh/memory.db` (project-local — preferred when working on TōSh)
3. `~/.tosh/memory.db` (global fallback)

Deletes are soft (tombstoned, never physically removed). User-sourced
memories require `confirm=true` to delete.

### Memory Schema

Every entry has: `content` (full text — what FTS matches), `summary`
(≤120 chars — used when injecting under token pressure), `category`,
`tags`, `scope` (`project` | `global`), `visibility` (`private` |
`shared`), `source` (`ai` | `user`), and an optional `session_id`.

Categories:

| Category     | Use for |
|--------------|---------|
| `fact`       | Stable project facts ("the binder pass runs between parse and evaluate") |
| `preference` | User style/workflow preferences ("prefers terse commit messages") |
| `pattern`    | Recurring solution patterns ("for new commands, register in CommandRegistry.cs and add metadata") |
| `decision`   | Architectural choices ("Tosh.Core is being phased out — don't add to it") |
| `history`    | Session milestones worth remembering |
| `note`       | User free-form text |

Relationships between memories (`memory_relate`): `supersedes`,
`supports`, `contradicts`, `related_to`.

### When the Agent Should Use It

- **Start of a session**: call `memory_recall` with a query relevant to
  the user's request to pull in prior decisions, preferences, and
  project facts. Do this *before* doing exploratory grep/glob work.
- **After a decision is made or a preference stated**: call
  `memory_store`. Examples: the user asks for terse output style; a new
  architectural choice is made; a non-obvious gotcha is discovered;
  a pattern is established for how to add X.
- **Before a major change**: call `memory_list` with relevant filters
  (e.g. `category=decision`) to audit what the companion already knows.
- **When prior guidance is invalidated**: store the new memory, then use
  `memory_relate` with `supersedes` pointing at the old one. Don't just
  forget — preserve the audit trail.

Do not store transient details (single-file edit history, intermediate
debug output). Memories are for things that should outlive the current
session.

## Language Syntax Quick Reference

### Variables

```tosh
var x = 42                         # declare a local variable
var name = "world"                 # string
var list = [1, 2, 3]               # list
var person = {| name: "Alice", age: 30 |} # record
var map = {% "name" => "Alice", "age" => 30 %} # dictionary

# After declaration, use $ prefix to reference or modify:
$x = 100                           # modify existing variable
echo $x                            # use variable
```

### Environment Variables

```tosh
# READ — use $env namespace (case-insensitive):
echo $env.HOME                    # /home/user
echo $env.path                    # works (case-insensitive)

# WRITE — either form works:
export MY_VAR = "hello"            # sets env var for this process + children
export PATH = "/usr/local/bin:$env.PATH"
$env.MY_VAR = "hello"              # equivalent — routes through the same export path
$env.path = "/extra:$env.PATH"     # case-insensitive: updates existing PATH
```

### Strings

```tosh
'single quotes are literal'
"double quotes allow \n escapes"
$"interpolated: ${expr} or $variable or $env.HOME"
```

### Functions

```tosh
# One-liner
func greet => echo "hello"

# With body
func greet(name) {
    echo $"Hello, {$name}!"
}

# Tosh has NO 'alias' keyword. Use one-liner functions instead:
func ll => ls -la
func gs => git status
```

### Classes and Modifiers

```tosh
class Point(x, y) {
    prop X = x
    prop Y = y
    func distance(other) { echo (Math.Sqrt(($this.X - $other.X) ** 2 + ($this.Y - $other.Y) ** 2)) }
    static func origin() { return new Point(0, 0) }
}
var p = new Point(3, 4)
echo $p.X                         # 3
echo (Point.origin())             # Point instance
```

#### Operator Overloading

Classes may overload binary operators by declaring methods named with
the operator symbol. The right-hand operand is passed as the argument:

```tosh
class Test {
    prop Value: int = 0
    func +(other) { $this.Value + $other.Value }
}
var x = new Test(); $x.Value = 4
var y = new Test(); $y.Value = 8
$x + $y                           # 12
```

Overloadable symbols: `+ - * / // % ** == != < <= > >= =~ !~`.

**Operator dispatch is symmetric:** For all overloadable and comparison operators, both operands are checked for overloads. The left operand is checked first; if no overload is found, the right operand is checked. If neither defines an overload, the built-in operator is used. This matches C#-like behavior and allows mixed-type arithmetic and comparisons to resolve to either operand's overload.

Unary operators and compound assignment (`+=`, etc.) are **not** directly overloadable (but `+=` desugars to `+`, so overloading `+` covers it).

#### Generic Classes

```tosh
class Box<T>(initial: T) {
    prop value: T = $initial
    func unwrap() -> T { return $this.value }
    func set(v: T) { $this.value = $v }       # strict: $v must already be a T
}

var bi = new Box<int>(42)         # T bound to int
var bs = new Box<string>("hi")    # T bound to string
# new Box<string>(42)             # ERROR: int is not a string (no coercion)

# Generic inheritance — type-args propagate through the chain:
class Pair<A, B> { hollow prop a: A  hollow prop b: B }
class IntPair extends Pair<int, int> {
    overrule prop a: A
    overrule prop b: B
    IntPair(a, b) { $this.a = $a; $this.b = $b }
}
```

Type-parameter-bound parameters, properties, and return values use
**strict** `IsInstanceOfType` checks at runtime — no widening,
stringification, or other coercion. This applies to ctor params,
method params, method return values, and `$this.X = …` assignment.
Ordinary (non-type-parameter) annotations still go through the
standard value-conversion path.

##### Type-Parameter Constraints

Add a `where T: <Constraint>[, <Constraint>…]` clause after the class
header to restrict acceptable type arguments. Built-in constraints:

| Constraint            | Satisfied by |
|-----------------------|--------------|
| `Numeric` / `Number` / `INumber` | `int`, `long`, `short`, `byte`, signed/unsigned variants, `float`, `double`, `decimal`, `Half`, `BigInteger` |
| `Add` / `Sub` / `Mul` / `Div` | Any numeric type, or any CLR type with the matching `op_*` static method |
| `Comparable`          | Any type implementing `IComparable` |
| `Eq`                  | Always satisfied (placeholder) |

```tosh
class Box<T>(initial: T) where T: Numeric {
    prop value: T = $initial
    func bump(amount) { $this.value = $this.value + $amount }
}

var bi = new Box<int>(10);    $bi.bump(5)       # 15
var bf = new Box<double>(1.5); $bf.bump(2.5)    # 4
# new Box<string>("hi")  → rejected: 'string' does not satisfy 'Numeric'
```

Multiple `where` clauses may follow each other to constrain different
type parameters. Unknown constraint names are accepted conservatively
(reserved for future user-defined constraints).

The same constraint names also work as right-hand operands for the
`is` / `is-not` operators on values, so runtime checks reuse the same
registry as compile-time generic-parameter validation:

```tosh
var x = 42
$x is Numeric           # true
$x is Comparable        # true
"hello" is Numeric      # false
$x is-not Numeric       # false
```

#### Class-Level Modifiers

Each modifier accepts a flavored TōSh name and a familiar canonical alias. Either form is valid; the canonical name is shown first below.

| Modifier (canonical / flavored) | Meaning |
|---------------------------------|---------|
| `sealed`                        | Cannot be inherited |
| `abstract` / `hollow`           | Cannot be instantiated; subclasses must override abstract methods |
| `static` / `hermit`             | Static-only class — all members auto-promoted to shared; no constructors |
| `strict`                        | All properties are read-only (immutable) after init |
| `partial`                       | Definition can be split across multiple declarations (all must say `partial`) |

#### Member-Level Modifiers

| Modifier (canonical / flavored) | Applies to    | Meaning |
|---------------------------------|---------------|---------|
| `private` / `shy`               | prop, func    | Hidden from outside the class |
| `public` / `proud`              | prop, func    | Explicitly public (default; no-op) |
| `protected` / `guarded`         | prop, func    | Visible to class + subclasses only |
| `static` / `shared`             | prop, func    | Belongs to the class, not an instance |
| `readonly` / `fixed`            | prop          | Read-only after initialization |
| `required` / `vital`            | prop          | Construction fails without a value |
| `override` / `overrule`         | func          | Override an inherited method |
| `abstract` / `hollow`           | func          | No body; must be overridden in subclass |
| `obsolete` / `fading`           | prop, func    | Deprecated — emits warning to stderr on use |
| `lazy`                          | prop          | Defers initializer until first access (cached) |
| `local`                         | prop, func    | Internal — assembly/module visibility only |
| `raw`                           | func          | Marks method for unsafe/native interop |

### Control Flow

```tosh
if $x > 10 {
    echo "big"
} else {
    echo "small"
}

for $item in $list {
    echo $item
}

try {
    risky-command
} catch ($err) {
    echo $"Error: {$err}"
} finally {
    cleanup
}
```

### Postfix Conditionals (`if` / `unless`)

Jump statements (`return`, `yield`, `throw`, `break`, `continue`) accept a
trailing `if <cond>` or `unless <cond>` guard:

```tosh
func find(items, pred) {
    for $x in $items {
        return $x if ($pred $x)        # only return when pred is truthy
    }
    return null
}

func emit-evens(n) {
    for $i in 0..$n {
        yield $i if ($i % 2 == 0)
    }
}

throw "bad input" unless (validate $input)
break    if $done
continue unless $found
```

- `stmt if   cond` runs `stmt` when `cond` is truthy.
- `stmt unless cond` runs `stmt` when `cond` is falsy (no negation
  operator is synthesised — the condition is preserved verbatim).
- The keyword must be on the same logical line as the statement.
- The condition is parsed as a single argument expression; parenthesise
  anything more complex than a bareword or `$variable`.
- Only jump statements support this form. `echo "hi" if $x` does *not*
  work; use a block `if` instead.

### Pipes and Redirects

```tosh
ls -la | where _.Type == file | sort-by Size | head 10
cat file.txt | grep "pattern" | wc -l
echo "hello" out> output.txt
echo "more" out>> output.txt
```

1. Pipes use "out>"/"o>", "err>"/"e>", "o+e>", and '>>' variants of those

### Special Namespaces

```tosh
$env.HOME              # environment variables (read-only namespace, use `export` to write)
$tosh.Config.*         # shell configuration (TTY, prompt, keybindings, etc.)
$tosh.Config.Shell.Dirs  # directory aliases dict
$tosh.IsLoginShell     # boolean: true when started as a login shell (-/--login)
```

The full `System.*` namespace tree is also exposed via the CLR resolver,
so `System.IO.File.OpenRead`, `System.IO.Path.GetFileName`,
`System.Convert.ToBase64String`, etc. work directly without an `import`.

### Picking Columns vs Rows

`get` and `row` form the canonical cluster for slicing pipelines:

```tosh
ls | get Name Size              # column picker — variadic field projection
ls | get Name                    # single column (returns scalars)
[10,20,30,40,50] | row 2         # row picker — single index
[10,20,30,40,50] | row 4 0 2     # variadic, yields in requested order: 50, 10, 30
[10,20,30,40,50] | row [3,1,0]   # list literal works too
[10,20,30,40,50] | row 1..3      # contiguous range
```

`select` and `pick` are soft aliases for `get`. Bad indices in `row`
throw `tosh.row.index_out_of_range`.

### Structured Introspection

`members` and `methods` are the canonical introspection commands. Both
accept subcommands:

```tosh
$obj | members                   # list every member (props, fields, methods, events)
$obj | members has Length        # → bool: does $obj's type have a Length member?
$obj | members get Length        # → descriptor record for Length, or empty
$obj | members props             # filter to properties only
$obj | members fields            # filter to fields only
$obj | members methods           # filter to methods only
$obj | methods has ToUpper       # bool — methods supports has/get only
$obj | methods get ToUpper       # descriptor(s)
$obj | props                     # shortcut for `members props`
$obj | funcs                     # shortcut for `methods`
```

The same forms accept a type name as a trailing arg:
`members has Length string`, `props string`, `methods has ToUpper string`.

### Other Type-Definition Forms

Tosh has lighter-weight cousins of `class` for common shapes:

```tosh
# record — concise data class. Constructor params become public properties.
record Point(x, y)
var p = new Point(1, 2)
echo $p.x                          # 1

# enum — named constant set
enum Color { Red, Green, Blue }
echo Color.Red

# module — namespace for grouping functions and constants
module Geometry {
    func area(r) { return 3.14159 * $r * $r }
}
Geometry.area 5
```

`interface` and `union` are also recognised; see the spec for details.

### Defer Blocks

```tosh
func process-file(path) {
    var fh = open $path
    defer { close $fh }              # always runs on scope exit
    # ... use $fh ...
}
```

`defer` blocks execute on scope exit (normal return, exception, or
explicit jump). Multiple `defer` blocks run in reverse order.

### Anonymous Type / Name Introspection

```tosh
var foo = 42
echo (nameof($foo))                  # "foo"  (note: $-prefix + parens required)
```

### `eval` — Evaluate a String as TōSh Source

`eval` parses and runs a string in the **current** engine session, so any
variables, functions, and imports defined around it are visible. All
arguments are joined with single spaces, and every value the inner
pipeline yields is streamed back to the caller.

```tosh
eval "1 + 2"                              # 3
eval 'echo hello'                         # hello

var name = "world"
eval $"echo Hello, {$name}!"              # Hello, world!

# Common pattern — dispatch a member by name on a type-valued variable:
read-lines colors.txt | each { eval $"System.Drawing.Color.{$_}" }
```

Notes & gotchas:

- Calling `eval` with no arguments throws — at least one source string is
  required.
- The source is parsed with source name `<eval>`, so diagnostics point at
  the eval'd text, not the surrounding script.
- `eval` shares the running engine — it is **not** a sandbox. Treat the
  argument like any other code path: don't feed it untrusted input.
- For dynamic member lookup on instances, prefer `members get`,
  `Color.FromName(...)`, or fluent access; `eval` is the right tool when
  you need to splice arbitrary TōSh syntax (operators, pipelines, etc.).

### Null-Coalescing Operators

```tosh
var x = null
echo ($x ?? "default")               # "default"

var y = "set"
$y ??= "ignored"                     # ??= is a no-op when LHS is non-null
echo $y                              # "set"
```

⚠️ `??=` requires the variable to already exist (declared with `var`).
Assigning to an undeclared name fails.

### `if` as an Expression

When wrapped in parentheses, `if`/`else` produces a value:

```tosh
var label = (if ($score >= 60) { "pass" } else { "fail" })
```

The bare statement form (`var x = if ...`) is **not** supported — the parens are required.

### Switch Statements

```tosh
switch ($code) {
    case 200 { echo "ok" }
    case 404 { echo "not found" }
    default  { echo $"unknown: {$code}" }
}
```

The value after `switch` must be parenthesised.

### Spread / Rest Parameters

```tosh
func log-all(args...) {
    for $a in $args { writeline $a }
}
log-all "first" "second" "third"     # collects positional args into a list
```

### Process Substitution

```tosh
diff <(sort file1.txt) <(sort file2.txt)    # treat command output as a file path
```

The `<(...)` form spawns the inner pipeline and exposes its stdout as a
named pipe path. `>(...)` is the write-side variant.

## Common Gotchas

1. **Two equivalent ways to set env vars.** Both `export NAME = "value"` and `$env.NAME = "value"` work and route through the same export path. The latter is case-insensitive against existing variables.

2. **No `alias` keyword.** Use `func name => command` for one-liner aliases.

3. **`export` uses `=` syntax**: `export NAME = "value"` — not `export NAME "value"` or `export NAME=value`.

4. **Variable declaration** uses `var`: `var x = 42` to declare, `$x = 100` to modify after.

5. **String interpolation** uses `$"..."` with `$variable`, `$env.VAR`, or `${expression}`.

6. **Single quotes are literal** — no variable expansion or escape sequences.

7. **`if` as an expression requires parens**: `var x = (if ... { ... } else { ... })`. The unparenthesised form is parsed as a command.

8. **`switch` requires parens around the value**: `switch ($x) { ... }`.

9. **`nameof` requires `$` and parens**: `nameof($foo)`, not `nameof foo`.

10. **`??=` requires an existing value** (the variable must have been declared with `var` first).

## Binder Pass

Between parse and evaluate, a static binder pass walks the AST and emits
`tosh.bind.unknown_command` diagnostics for command names that look like
typos for registered builtins or aliases (Levenshtein ≤ 1 for short names,
≤ 2 for longer). Same-source `func` declarations are recognised and never
flagged. Strictness depends on context:

| Context                          | Default         |
|----------------------------------|-----------------|
| Interactive REPL                 | `Warn` (stderr) |
| `tosh -c "..."`                  | `Strict`        |
| `tosh script.tosh` / `source`    | `Strict`        |
| `profile.tosh` / `autoload/*`    | `Warn`          |

To bypass the binder entirely (recovery escape hatch — undocumented in
user-facing help), set `TOSH_DISABLE_BINDER=1`.

## Startup File Load Order

When tosh starts as a login shell (`-` prefix in argv[0] or `--login`):

1. `~/.config/tosh/config.tosh` — shell configuration (prompt, keybindings, TTY settings)
2. `~/.config/tosh/profile.tosh` — environment setup, exports, user functions
3. `~/.config/tosh/autoload/*.tosh` — alphabetically sorted, top-level `.tosh` files only

When the shell exits, `~/.config/tosh/logout.tosh` (if present) runs as the
final step — use it for cleanup commands. Errors in any startup or logout
file are logged to stderr but do not prevent the shell from starting/stopping.
Use `--safe` to skip all startup files, or `--no-profile` to skip profile +
autoload.

## CLI Flags

```
tosh                              # interactive REPL
tosh -c "command"                 # execute command string
tosh script.tosh                  # execute script file
tosh --login                      # login shell mode
tosh --no-startup                 # skip config.tosh, profile.tosh, and autoload/
tosh --no-profile                 # skip profile.tosh only (config.tosh and autoload/ still load)
tosh --safe                       # skip all startup files, and say so on stderr
tosh --version                    # print version
tosh --help                       # print help
tosh --profile-startup            # print phase-by-phase startup timing
tosh --diagnostics=text|plain|json  # override diagnostic output mode
tosh -- script-with-leading-dash  # '--' stops flag parsing for the next arg
tosh --export-command-metadata    # dump all builtin command metadata as JSON
tosh --export-command-metadata --latex   # dump as LaTeX
tosh --export-command-metadata --vscode  # dump as VS Code format
tosh --dump-builtins              # alias for --export-command-metadata (JSON)
```

## Introspecting Commands at Runtime

```tosh
help ls                           # returns a HelpTopic object
help ls | to json                 # full structured metadata as JSON
help ls | get Usage               # just the usage string
apropos "file"                    # search commands by keyword
```

## Machine-Readable Command Metadata

```bash
# Dump all ~209 built-in commands with full signatures, args, options, examples:
tosh --dump-builtins
tosh --export-command-metadata

# The MCP server also exposes a `command_metadata` tool for AI agents.
```

## Built-in Command Categories

TōSh has ~209 built-in commands spanning these categories:

- **Filesystem**: ls, cd, pwd, mkdir, touch, rm, cp, mv, chmod, chown, find, glob, tree, stat, link, mktemp, readlink, realpath, dirname, basename
- **Text/IO**: cat, read, write, append, head, tail, wc, grep, cut, tr, uniq, lines, read-lines, read-bytes, write-bytes, open, close, tee
- **Data/Format**: from, to, parse, split, join, replace, match, template, hash, get, row, rename, inspect
- **Functional**: where, each, map, filter, reduce, scan, flatmap, zip, first, last, skip, sort, reverse, count, collect, flatten, distinct, group-by, chunk, window, partition, frequencies, transpose, interleave
- **Aggregation**: sum, average, min, max, summarize
- **System**: uname, hostname, whoami, id, ps, kill, signal, jobs, fg, bg, uptime, free, df, du, lsblk, lscpu, lsfd, lsipc, systemctl, journalctl, loginctl, hostnamectl, networkctl, findmnt
- **Environment**: env, vars, export, forget/unset, which
- **Networking**: ping, http, ip (13 structured subcommands)
- **Time**: date, time, timespan, sleep
- **Shell**: echo, clear, history, config, exit, exec, source, assert, eval
- **Object/CLR**: typeof, describe-type, members, methods, props, funcs, constructors, types, load-assembly, new-object, cast. The verb-form commands (`call`, `call-method`, `get-prop`, `get-props`, `get-methods`, `set-prop`, `del-prop`, `has-prop`, `has-method`) are deprecated — prefer fluent member access (`$obj.Method($args)`, `$obj.Prop`, `$obj.Prop = value`) and `members has X` / `methods has X` for introspection.
- **Prompt**: prompt-time, prompt-dir, prompt-git, prompt-userhost, prompt-history, prompt-jobs, prompt-duration, prompt-exitcode, prompt-text, prompt-newline
- **Interop**: native-alloc, native-free, native-read, native-write, native-sizeof, native-offsetof. The `native-*` names are canonical; `alloc`, `read-buffer`, `write-buffer`, `size-of`, and `offset-of` are blessed aliases. `native-free` has no alias — use `forget $buffer`, which unsets the variable and frees the buffer in one step. Byte offsets are the `--at` flag, not a positional slot.
- **Native declarations** (language keywords, not commands): `raw struct` / `raw union` declare a C memory layout; `bind native "lib" { func … }` binds exports, and works inside a module or class body; `raw func … from "lib"` is the single-binding form. Return conventions are `-> ok` (0 is success), `-> count` (>= 0 is success and is the value), `-> auto` (project out params, no checking), or an explicit `where (…)`. Failures throw `NativeError` carrying errno.
- **Path Predicates**: exists, is-file, is-dir, is-link

## Writing Config Files

Commands in config/profile/autoload files use the same syntax as the interactive shell.
There is no separate scripting syntax.

```tosh
# ~/.config/tosh/profile.tosh — typical setup
export EDITOR = "nvim"
export PATH = "$env.HOME/.local/bin:$env.PATH"

$tosh.Config.Shell.Dirs["projects"] = "$env.HOME/projects"

func ll => ls -la
func gs => git status
func mkcd(dir) {
    mkdir $dir
    cd $dir
}
```

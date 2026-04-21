# TōSh Backlog

Open work items by area, roughly ordered by priority within each section.

Last updated: April 19, 2026 (evening).

---

# REPL Line Editor (In Progress)

Derived from [Line Editor RFC](LINE_EDITOR_RFC.md). Phase 1 is focused on safety and deterministic editing.

## Phase 1 Checklist

- [x] Preserve multiline draft while navigating wrapped lines
- [x] Fix continuation indentation growth in multiline editing
- [x] Add foundational undo/redo support in `LineEditorBuffer`
- [ ] Add edit transaction grouping (coalesced typing, completion-accept as single transaction)
- [ ] Add explicit draft snapshot/restore model around history traversal
- [ ] Add multiline history traversal modifiers (`Alt+Up` / `Alt+Down`) without draft clobber
- [ ] Add focused tests for key behavior matrix in multiline + completion contexts

### Priority: P0

---

# Language Features & Paradigms (Planned)

## Comprehensions — Future Extensions

Core comprehensions (list, set, dict, generator) are implemented. Remaining work:

- Cartesian product syntax: `for x in 1..5, y in 1..5`
- Parallel/zip comprehensions: `for x in $a || y in $b`
- Pattern destructuring in generators: `for (k, v) in $dict`
- True lazy generator expressions (currently eager)

### Priority: P2

---

## Lazy Infinite Lists & Self-Referential Sequences

Haskell-style lazy evaluation for infinite, self-referential data structures.

### Motivation

Stateful sequences like Fibonacci can't be expressed tersely with comprehensions
alone (comprehensions map/filter over sources; they don't carry accumulator state).
TōSh already has `unfold` for this, but it's verbose:

```tosh
# Current TōSh — functional but dense:
func f(c) => unfold [0, 1] { [$_[0], [$_[1], ($_[0] + $_[1])]] } | first $c
```

With lazy infinite lists and self-reference, this becomes:

```tosh
# Haskell-style self-referential lazy list:
var fibs = lazy [0, 1, ...zip-with (+) $fibs ($fibs | skip 1)]
$fibs | first 20
```

### Syntax Proposals

#### `lazy` keyword for deferred evaluation
```tosh
var naturals = lazy [1, 2, ...]          # inferred continuation (sugar for 1..)
var fibs = lazy [0, 1, ...zip-with (+) $fibs ($fibs | skip 1)]
var powers = lazy [$x ** 2 <| for $x in 1..]   # lazy comprehension (already covered by generator expr)
```

#### `unfold` comprehension shorthand
```tosh
# Terse unfold-style with tuple destructuring:
var fibs = [($a, $b) = (0, 1) <| unfold ($b, $a + $b)]

# Or with a dedicated iterate/recurrence form:
var fibs = iterate (0, 1) func($a, $b) => ($b, $a + $b) | map first
```

#### `recur` / `iterate` built-in
```tosh
# iterate applies a function repeatedly to a seed:
var powers_of_2 = iterate 1 func($x) => $x * 2
# => lazy [1, 2, 4, 8, 16, ...]

# recurrence relation (multi-value seed):
var fibs = recur (0, 1) func($a, $b) => $a + $b
# => lazy [0, 1, 1, 2, 3, 5, 8, ...]
```

### Design Notes

- `lazy [...]` produces a lazily-evaluated sequence (like Haskell lists or C# `IEnumerable`).
- Self-referential bindings require the runtime to support thunks / memoized lazy cells.
- `...expr` inside a lazy list = spread/continuation from a generator expression.
- The `iterate` / `recur` forms are syntactic sugar over `unfold` but much more readable.
- Lazy sequences compose naturally with pipelines: `$fibs | where (_ % 2 == 0) | first 10`.
- Memoization: once a cell is forced, cache the result (avoid recomputation on re-traversal).

### Open Questions

- Should all sequences default to lazy, or only when explicitly marked `lazy`?
- Should `unfold` syntax integrate with comprehension `<|` form?
- How does laziness interact with side-effects in pipelines?
- Should lazy lists support `take-while` natively for termination?

### Priority: P1

---

## Macro System (AST Macros)

Boo-inspired AST-level macros that look and feel like native keywords.

### Concept
```tosh
# Usage — looks like a built-in:
retry 3 {
    http get $url
}

timeout 5s {
    slow-operation
}

# Definition — a class/function that receives the AST block:
macro retry($count, $body) {
    for $i in 1..$count {
        try {
            $body
            return
        } catch $err {
            if $i == $count { throw $err }
        }
    }
}
```

### Design Notes

- Macros receive their arguments + body as AST nodes, not evaluated values.
- The macro expands/transforms the AST before evaluation.
- Unknown bare words are resolved by searching for a `<Name>Macro` class
  (Boo pattern) — this makes the language extensible without parser changes.
- Potential built-in macros: `retry`, `timeout`, `benchmark`, `suppress`,
  `with`, `transaction`, `parallel`, `watch`.
- Community macros could be distributed as modules.

### Priority: P2

---

## Enhanced LINQ-like Features

### Query Expression Syntax
```tosh
var result = from $p in $products
             where $p.Price > 50
             join $c in $categories on $p.CategoryId == $c.Id
             orderby $p.Price descending
             select { Name: $p.Name, Category: $c.Name, Price: $p.Price }

var summary = from $sale in $sales
              group $sale by $sale.Region into $g
              select { Region: $g.Key, Total: ($g | sum _.Amount), Count: ($g | count) }
```

### New Pipeline Operators

| Operator | Example | Notes |
|----------|---------|-------|
| `join` | `$orders \| join $customers on _.CustomerId == _.Id` | Inner join |
| `left-join` | `$orders \| left-join $customers on _.CustomerId == _.Id` | Left outer join |
| `cross-join` | `$xs \| cross-join $ys` | Cartesian product |
| `group-join` | `$depts \| group-join $employees on _.Id == _.DeptId` | Hierarchical grouping |
| `orderby` | `$data \| orderby _.Score descending, _.Name` | Multi-key sort with direction |
| `distinct-by` | `$items \| distinct-by _.Category` | Keep first per key |
| `aggregate` | `$data \| aggregate { Sum: (sum _.Val), Avg: (average _.Val) }` | Multi-aggregate |
| `window-func` | `$rows \| window-func (rank) over (partition-by _.Dept orderby _.Salary)` | SQL window functions |
| `let` | `$data \| let _.Score = (_.Hits / _.Views * 100) \| where _.Score > 75` | Computed columns |
| `into` | `... \| into $varName` | Capture mid-pipeline |
| `tee` | `... \| tee { $_ \| to json > debug.json } \| ...` | Side-effect tap |

### Method-Style Chaining on Collections
```tosh
var top5 = $products
    .where(_.Price > 10)
    .orderby(_.Rating, descending)
    .select(_.Name)
    .take(5)
```

### Priority: P2

---

## Scientific & Mathematical Features

### Matrix / Vector Types
```tosh
var m = matrix [[1,2,3],[4,5,6],[7,8,9]]
var v = vector [1, 2, 3]

$m * $m                         # matrix multiplication
$m | determinant
$m | inverse
$m | transpose                  # already exists
$m | eigenvalues
$v | magnitude                  # 3.7416...
$v | normalize
dot $v1 $v2
cross $v1 $v2
```

### Complex Numbers
```tosh
var z = 3 + 4i
$z | magnitude                  # 5.0
$z | phase                      # 0.9272 rad
$z | conjugate                  # 3 - 4i
$z | to-polar                   # (5.0, 0.9272)
```

### Symbolic Math (aspirational)
```tosh
var expr = sym "x^2 + 2*x + 1"
$expr | differentiate "x"       # 2*x + 2
$expr | integrate "x"           # x^3/3 + x^2 + x
$expr | factor                  # (x + 1)^2
$expr | solve "x"               # [-1]
```

### Units System Expansion
```tosh
var force = 10kg * 9.8m/s^2     # 98 N (auto-derive)
var energy = $force * 5m        # 490 J
$energy | convert-to kWh
100°C | convert-to °F           # 212°F
```

### Priority: P2 (matrix/vector, complex), P3 (symbolic, units)

---

## Relativistic Programming Concepts

Inspired by relativistic programming (RCU, causal consistency) — design
concurrent primitives that tolerate ordering conflicts rather than preventing them.

### Parallel Pipelines with Relaxed Ordering
```tosh
parallel {
    http get "https://api-a.com/data" | as $a
    http get "https://api-b.com/data" | as $b
    http get "https://api-c.com/data" | as $c
} | merge --causal
```

### Structured Concurrency
```tosh
# First result wins, others cancelled:
race {
    http get "https://primary.api/data"
    http get "https://backup.api/data"
}

# Wait for all, collect successes and failures:
settle {
    ping host-a
    ping host-b
    ping host-c
} | where _.Status == "fulfilled"
```

### Reactive / Observable Pipelines
```tosh
var config = watch "config.yaml" --snapshot
# Consumers see consistent snapshots even as the file changes
```

### Shared State with Atomic Operations
```tosh
shared var $counter = 0
parallel-each $urls func($url) {
    $result = http get $url
    atomic { $counter = $counter + 1 }
}
```

### Stream Processing with Temporal Windows
```tosh
tail -f /var/log/app.log
    | parse "{timestamp} {level} {message}"
    | window 5s sliding 1s
    | group-by _.level
    | summarize { Count: count, Rate: (count / 5) }
```

### Priority: P3

---

## Boo-Inspired Ergonomics

### `unless` keyword
```tosh
echo "ok" unless $failed
```

### Postfix conditionals
```tosh
return $x if $valid
skip unless $ready
```

### Slicing syntax
```tosh
$list[1:3]                      # elements 1..2
$str[:-1]                       # all but last char
$arr[::2]                       # every other element
```

### `isa` operator
```tosh
if $obj isa Point { echo $obj.X }
```

### Callable types
```tosh
var predicate: func(int) -> bool = func($x) => $x > 0
```

### Priority: P2

---

## Miscellaneous Questions

- Should we allow user-defined collection types?
- Should comprehensions be first-class values (passable as arguments)?
- Should macros be able to define new comprehension forms?
- Should `<|` be overloadable for custom monadic comprehensions?

---

### Priority Legend

| Level | Meaning |
|-------|---------|
| P1 | Highest priority — core language improvement |
| P2 | High priority — major ergonomics / extensibility |
| P3 | Advanced — scientific, concurrency, or aspirational |

---

## Class & Type System

| Feature | Description |
|---------|-------------|
| Generics | Generic type parameters on classes, interfaces, methods (e.g. `class Stack<T>`) |
| Operator overloading | Custom `+`, `-`, `*`, `/`, `[]`, etc. on class instances |
| Interface default methods | Allow method bodies in interfaces as default implementations |

## Unix Command Parity

### Adapters

| Command | Remaining gaps |
|---------|----------------|
| `ip` | remaining: monitor, stats, macsec, l2tp, xfrm, fou, ila, ioam, seg6 |

## TUI Platform

- Build future full-screen tools on the shared runtime
- Form editors and structured input widgets

## AI Companion Interop

### Tools
- VS Code extension: syntax highlighting, bracket matching, comment toggling for `.tosh` files
- MCP server enhancements: additional tools (e.g. `run_snippet`, `explain_error`, `suggest_command`)
- Structured error output mode for machine-consumable diagnostics

### Documentation
- Language reference: formalize the existing LaTeX spec into a living reference AI agents can query
- Expand `AGENTS.md` as the language and shell evolve

### Project Memory
- Create a persistent / scalable project memory storage that can be used by any AI Companion

---

## Completed

### AI Companion foundations ✓

- **AGENTS.md**: Created comprehensive AI agent reference with syntax quick-ref, common gotchas, CLI flags, startup load order, 209+ builtin categories, and machine-readable metadata instructions.
- **MCP `command_metadata` tool**: Added 7th MCP tool exposing all builtin command metadata (signatures, args, options, examples) with optional `name` and `category` filters.
- **`--dump-builtins` CLI flag**: Added as alias for `--export-command-metadata` for quick JSON metadata export.
- **Better error messages**: Shell migration hints (`alias` → `func`, `set` → `var`/`export`, etc.), Levenshtein "did you mean" suggestions for typos, clear error when assigning to `$env.X` directly.
- **`export NAME = value` syntax**: Changed from `export NAME "value"` to `export NAME = value` for consistency with `var` declarations. Parser guard prevents `export`/`global`/`shy` from being misinterpreted as type names in typed variable declarations.

### TUI widget extraction ✓

Extracted 12 shared rendering methods from HelpBrowserScreen (~3400 lines) and ConfigBrowserScreen (~3500 lines) into `TuiRenderHelpers.cs`:

- **Borders:** `RenderTopBorder`, `RenderBottomBorder`
- **Box content:** `RenderBoxContentLine`, `RenderStyledBoxLine` (multi-segment)
- **Segments:** `RenderStyledSegments` (general-purpose styled segment renderer)
- **Layout:** `RenderSearchRow`, `RenderDualPaneContent` (dual-pane orchestrator with delegates)
- **Footer:** `RenderFooterLine`
- **Text:** `TrimOrPadPlain`, `ClipPlain` (ANSI-aware)
- **Style:** `MergeListStyles`, `FormatBoolean`

Both browser screens now delegate to shared helpers instead of maintaining duplicate rendering code. ~200 lines eliminated.

### `ip` subcommand expansion ✓

Added 7 structured subcommands: tunnel, tuntap, vrf, maddr, mroute, token, ntable. Total structured coverage: 13 subcommands (addr, link, route, neigh, rule, netns, tunnel, tuntap, vrf, maddr, mroute, token, ntable). Each includes typed records, JSON parser, display profiles with column builders, and unit tests. Added missing IpNetns display profile.

### `match` as pattern-matching expression ✓

`match` is now a full pattern-matching expression supporting value, type (`is`), comparison (`>`, `>=`, `<`, `<=`), range (`..`), regex (`=~`), and guard (`if`) patterns. The `_` prefix is required before comparison and type-check patterns to avoid ambiguity with redirection operators. Plain value arms and `default` do not require the prefix.

### Tuple and set literals ✓

First-class literal syntax: `(1, 2)` for tuples, `{: 1, 2, 3 :}` for sets.

### Display profile system ✓

Type-based display profiles control table columns, ordering, and cell rendering.

### Comprehensions (list, set, dict, generator) ✓

Full comprehension syntax with `<|` operator. All four collection types: `[body <| for x in source]` (list), `{: body <| for x in source :}` (set), `{ key => value <| for x in source }` (dict), `(body <| for x in source)` (generator). Supports `where` filtering, `let` bindings, and nested `for` clauses. 14 tests.

### Math namespace & statistics commands ✓

Math static type with 35+ functions (trig, log, combinatorics, etc.) and constants (PI, E, Tau, Infinity, NaN, Epsilon). Statistical pipeline commands: `median`, `stdev`, `variance`, `percentile`, `describe`. 31 tests.

### Login shell preparation ✓

`IsLoginShell` is now set before startup loading so `$tosh.IsLoginShell` is visible in config/profile scripts. Login shells set `SHELL` to the tosh executable path and ensure its directory is on `PATH`. SIGHUP and SIGTERM handlers kill jobs and exit cleanly. Arch Linux PKGBUILD registers `/usr/bin/tosh` in `/etc/shells`.

### Performance under volume ✓

Startup and rendering performance optimized across three rounds:

1. **R2R + uncompressed publish**: 265ms → 135ms `ls /usr/bin` (R2R precompiled code, eliminated ~95ms decompression penalty).
2. **uid/gid caching + column shrink**: 135ms → 124ms (cached P/Invoke lookups, proportional column reduction).
3. **ANSI early-exit + single-pass widths + profile cache**: 124ms → 100ms (skip regex for plain text, eliminate per-column LINQ scans, cache type resolution).

Current benchmarks (April 16, 2026):

| Benchmark | tosh | nushell | pwsh | bash |
|-----------|------|---------|------|------|
| Bare startup | 55ms | 5ms | 89ms | 0.5ms |
| With config | 73ms | — | — | — |
| ls /usr/bin | 100ms | 67ms | 366ms | 3.8ms |

The 55ms startup floor is .NET runtime initialization. NativeAOT is not feasible due to core use of `Reflection.Emit` (FFI delegate generation), `Activator.CreateInstance` (generic collection construction), and `Type.GetType` (runtime type resolution). Subtracting startup, tosh's per-operation throughput is competitive with nushell.

### Native/object/text boundary polish ✓

Three optimizations to the native command ↔ pipeline boundary:

1. **SplitLines deduplication**: Precompute total rendered line count during the data row loop and pass it to `ShouldRepeatHeaderAtBottom`, eliminating redundant re-splits. `PadCell`/`ClipCell` now use `GetVisibleLength()` directly since they operate on already-split single lines.
2. **ShellTextLine auto-unwrap**: `OperatorEvaluator.EvaluateBinary()` and `Matches()` unwrap `ShellTextLine` to its `.Text` at entry, so `==`, `=~`, `contains`, `starts-with`, `ends-with`, and all comparison operators work transparently on native command output without `.Text`.
3. **ExternalTextSerializer collection handling**: `IDictionary` serializes as key\tvalue lines, `IEnumerable` serializes one element per line, instead of falling through to useless `.ToString()`.

ls /usr/bin benchmark: 100ms → 96ms.

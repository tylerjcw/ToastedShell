# Ideas

Speculative language and shell directions — a thinking document, not a work list.
Extracted from BACKLOG.md on 2026-08-16 when that file was dissolved into
[the plan](plan/README.md).

Nothing here is committed to. An idea that gets agreed to becomes an item under
`docs/plan/items/`, at which point it acquires acceptance criteria and a status; the
rest stay here, which is the honest place for them. Sections marked shipped are kept
because the surrounding discussion explains why the feature took the shape it did.

---

# Language Features & Paradigms (Planned)

## Live / streaming command output ✓ Shipped

Long-running commands now stream rows incrementally to the REPL.
`IDisplaySink` ([src/Tosh.Cli/IDisplaySink.cs](../src/Tosh.Cli/IDisplaySink.cs)) is
implemented by `BufferingDisplaySink`, `StreamingTableSink`, and
`AutoDisplaySink` (the auto-decision wrapper). The CLI consumes pipelines
directly via `await foreach` over `engine.ExecuteAsync(...)`. Append-only
bordered rendering, TTY/streaming-hint decision rule, and Ctrl-C bottom-border
cancellation are all in place.

### Out of scope (still deferred)

- Re-render-on-update for dynamic widths (cursor-up + redraw).
- Alternate-screen `htop`-style live tables (belongs in `Tosh.Tui` widgets, not the shell).
- Pagination interaction (height overflow auto-falls-back to append-only).

### Priority: P1 — closed

## Comprehensions ✓ Shipped

Core comprehensions (list, set, dict, generator) are implemented, along
with Cartesian product (`for x in [1,2], y in [10,20]`), parallel/zip
(`for x in $a || y in $b`), and tuple destructuring (`for (a, b) in $pairs`).

The **bracket form `[...]` is eager** (materialised list); the
**parenthesised form `(...)` is lazy** — it returns a `LazySequence`
that evaluates the body on demand and composes naturally with infinite
sources like ranges, `iterate`, and `recur`.

Verified 2026-05-07:

```tosh
[($x + $y) <| for x in [1,2], y in [10,20]]      # Cartesian → [11, 21, 12, 22]
[$x + $y <| for x in [1,2,3] || y in [10,20,30]] # zip       → [11, 22, 33]
[$a + $b <| for (a, b) in [(1,2),(3,4)]]         # destruct  → [3, 7]
($x ** 2 <| for x in 1..) | first 5              # lazy      → [1, 4, 9, 16, 25]
```

See [ParseGeneratorComprehension](../src/Tosh.Language/Parsing/ToshParser.cs#L6581)
and the `GeneratorComprehensionArgumentSyntax` evaluation path in
[ToshEngine.cs](../src/Tosh.Language/ToshEngine.cs#L5444), which
returns a [`LazySequence`](../src/Tosh.Runtime/LazySequence.cs).

### Priority: P2 — closed

---

## Lazy Infinite Lists & Self-Referential Sequences

Haskell-style lazy evaluation for infinite, self-referential data structures.

### Status (2026-05-07)

Most of this section is already shipped:

- ✓ **`iterate` and `recur` built-ins** — see
  [IterateCommand.cs](../src/Tosh.Stdlib/Functional/IterateCommand.cs)
  and [RecurCommand.cs](../src/Tosh.Stdlib/Functional/RecurCommand.cs).
  Pair with `first` / `take-while` / `take-until` to bound:
  `iterate 1 func(x) => ($x * 2) | first 10`,
  `recur (0, 1) func($a, $b) => $a + $b`.
- ✓ **Lazy `<|` generator form** — the parenthesised comprehension
  `($x ** 2 <| for x in 1..)` produces a [`LazySequence`](../src/Tosh.Runtime/LazySequence.cs).
- ✓ **Infinite ranges** — `1..` is a `ToshRange { IsInfinite: true }`
  recognised by Cartesian product, comprehensions, and the
  `IsLazyOrInfinite` helper in [ToshEngine.cs](../src/Tosh.Language/ToshEngine.cs#L11084).
- ✓ **`lazy` modifier on class properties** — defers initializer until
  first access (see [ToshClassPropertyDefinition.IsLazy](../src/Tosh.Language/ToshClassPropertyDefinition.cs)
  and `_lazyInitialized` cache in [ToshClassInstance.cs](../src/Tosh.Language/ToshClassInstance.cs#L177)).
- ✏ Remaining: `lazy […]` literal syntax with **self-referential bindings**
  (`var fibs = lazy [0, 1, ...zip-with (+) $fibs ($fibs | skip 1)]`).
  Requires thunk/memoised-cell runtime semantics for the inner
  forward-reference to resolve as the same sequence being constructed.
  This is the only true gap; everything else listed under "Syntax
  Proposals" below already works in some form.

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

## Macro System (Runes) — ✓ Shipped

Boo-inspired AST-level macros are implemented under the name **runes**
(distinguishing them from the runtime-evaluated `func`).

### Surface syntax

```tosh
# Definition — looks like a function but receives unevaluated arg thunks:
rune unless(condition, body) {
    if (not $condition) {
        $body
    }
}

# Usage — looks like a built-in:
unless $failed (echo "ok")
with-retry 3 { http get $url }
benchmark "fetch" { http get $url }
dbg ($x + 1)
```

### Implementation

- Parser: `ParseRuneDefinitionStatement` ([ToshParser.cs:2420](../src/Tosh.Language/Parsing/ToshParser.cs#L2420)),
  `LooksLikeRuneDefinition` ([ToshParser.cs:9188](../src/Tosh.Language/Parsing/ToshParser.cs#L9188)).
- Runtime: [`RuneCommand`](../src/Tosh.Language/Bridge/RuneCommand.cs),
  [`RuneDefinition`](../src/Tosh.Language/RuneDefinition.cs),
  [`RuneThunk`](../src/Tosh.Language/RuneThunk.cs) (deferred-evaluation
  argument thunks).
- Built-ins: [`BuiltinRunes.cs`](../src/Tosh.Language/BuiltinRunes.cs)
  ships `dbg`, `unless`, `benchmark`, `with-retry`.
- Rune-level modifiers parsed today: `sealed` (default), `leaky`,
  `fixed`, `lazy`.

### Status: ✓ Closed

The shipped base set (`dbg`, `unless`, `benchmark`, `with-retry`) covers
the common ergonomic cases. Additional runes (`timeout`, `suppress`,
`with`, `transaction`, `parallel`, `watch`) are deliberately left for
user code — the whole point of runes is that user-defined macros are
first-class. Community runes can ship as ordinary `.tosh` modules.

### Priority: P2 — closed

---

## Help Topic Display Profile — `Default` column for Options

The `HelpTopicSummaryRenderer` (added 2026-05-08) renders single
`HelpTopic` instances with a layout modeled on `$tosh`: title bar,
description, and sub-boxes for Usage / Arguments / Options /
Pipeline / Examples / Related, with REPL-style syntax highlighting
on examples.

The Options sub-box now renders a third `Default` column when any
option in the list carries a literal default. Defaults flow through
`CommandOptionAttribute.Default` → `CommandOptionMetadata.Default` →
`HelpOptionInfo.Default` and are surfaced in `to json` /
`--export-command-metadata` and the MCP `command_metadata` tool.

### Work
- ✅ Extend `HelpOptionInfo` with `Default` (string?, optional).
- ✅ Backfill defaults in stdlib `[CommandOption]` metadata where
  obvious (`ls --sort`/`--time`, `ping --count`/`--timeout`/`--size`/
  `--interval`, `head`/`tail -n`, `cut -d`, `http --as`/`--bind`/
  `--index`, `prompt-time --format`, `tui --page-size`).
- ✅ Render a third column in `RenderOptionsSubBox` when any option
  in the list carries a default; keep two-column layout otherwise.
- ✅ Surface the field in JSON `to json` output and the MCP
  `command_metadata` tool.

### Priority: P3 — closed (2026-05-08)

---

## Enhanced LINQ-like Features

### Query Expression Syntax
```tosh
var result = from $p in $products
             where $p.Price > 50
             join $c in $categories on $p.CategoryId == $c.Id
             orderby $p Price descending
             select {| Name: $p.Name, Category: $c.Name, Price: $p.Price |}

var summary = from $sale in $sales
              group $sale by $sale.Region into $g
              select {| Region: $g.Key, Total: ($g | sum _.Amount), Count: ($g | count) |}
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
| `aggregate` | `$data \| aggregate {| Sum: (sum _.Val), Avg: (average _.Val) |}` | Multi-aggregate |
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
    | summarize {| Count: count, Rate: (count / 5) |}
```

### Priority: P3

---

## Boo-Inspired Ergonomics

### Slicing syntax ✓ Shipped
```tosh
$list[1:3]                      # elements 1..2
$str[:-1]                       # all but last char
$arr[::2]                       # every other element
```
Bracket-slice notation is in. `get` also accepts `ToshRange` for the
pipeline form (`... | get 2..5`, see [GetCommand.cs](../src/Tosh.Stdlib/Pipeline/GetCommand.cs#L40)).

### `is` / `is a` operator ✓ Shipped
```tosh
if $obj is Point { echo $obj.X }
if $obj is a Point { echo $obj.X }
```
No separate `isa` keyword is planned — `is` (and the multi-word
`is a` / `is not` / `is in` / `is not in`) covers it.

### `unless` keyword ✓ Shipped

Implemented as a built-in rune, not a parser keyword:

```tosh
unless $failed (echo "ok")
unless false (echo 5)              # → 5
```

See [BuiltinRunes.cs](../src/Tosh.Language/BuiltinRunes.cs) and the
macro/rune system above.

### Callable values ✓ Shipped

Typed-parameter lambdas with optional return-type annotations can be
bound to variables and invoked with `$name(args)`:

```tosh
var test = func(x: bool) -> bool { return (not $x) }
$test(true)                         # → false

var dbl = func(x: int) -> int => ($x * 2)
$dbl(7)                             # → 14
```

Parameter type annotations (`x: bool`, `n: int`, etc.) and the new
lambda-level return type annotation (`-> bool`) both flow through
`AnonymousFunctionArgumentSyntax.ReturnTypeName` into the runtime's
`CreateFunctionDefinition`. A dedicated `func(int) -> bool` first-class
signature *type* (for storing in a variable annotation) is not yet a
parser construct, but the practical capability is in.

### Postfix conditionals ✓ Shipped

```tosh
return $x if ($x > 5)               # early return when condition holds
break unless ($i < 5)               # break when condition fails
continue if ($i % 2 == 0)
throw "negative" if ($x < 0)
yield $row if ($row.Active)
```

Applies to `return`, `break`, `continue`, `throw`, and `yield`.
Implemented as a syntactic wrapper in the parser: when one of these
statements is followed (on the same line, no boundary) by `if <cond>`
or `unless <cond>`, the statement is wrapped in an
`IfStatementSyntax`. `unless` places the inner statement in the else
branch so the condition expression is preserved verbatim. See
`TryWrapPostfixConditional` in
[ToshParser.cs](../src/Tosh.Language/Parsing/ToshParser.cs).

### Priority: P2 — closed

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

| Feature | Status |
|---------|--------|
| Generics on classes / type aliases | ✓ Shipped — `class Stack<T>` / `type Pair<A, B> = ...` parse via `ParseTypeParameterList` ([ToshParser.cs:4637](../src/Tosh.Language/Parsing/ToshParser.cs#L4637)). End-to-end runtime support landed 2026-05: type-argument resolution at instantiation, strict no-coercion enforcement of constructor / method / property bindings, return-type substitution, inheritance binding propagation through `extends Base<T>`, generic-class annotations recognised by `IsKnownAnnotatedType`, and compiled-mode dispatch via `ToshHost.NewObject(typeName, bareTypeName, string[] typeArgs, object?[] args)`. See [GenericClassTests.cs](../tests/Tosh.Tests/GenericClassTests.cs). |
| Function / method overloading | ✓ Shipped — same-name funcs with distinct arities/typed signatures merge through `OverloadedFunctionCommand` ([src/Tosh.Language/Bridge/OverloadedFunctionCommand.cs](../src/Tosh.Language/Bridge/OverloadedFunctionCommand.cs)) and `ToshClassDefinition` overload resolution. |
| Operator overloading | ✓ Parser-level wired — `IsOverloadableOperatorToken` lets classes declare `+`, `-`, `*`, `/`, `[]`, etc. as method names ([ToshParser.cs:4903](../src/Tosh.Language/Parsing/ToshParser.cs#L4903)). |
| Interface default methods | Open — allow method bodies in interfaces as default implementations. |

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
- ✓ VS Code extension: syntax highlighting, bracket matching, comment toggling for `.tosh` files — ships at [editor/vscode/tosh.tosh-lang](../editor/vscode/tosh.tosh-lang).
- ✓ MCP server enhancements: `run_snippet` and `explain_error` tools shipped in [src/Tosh.Mcp/ToshMcpServer.cs](../src/Tosh.Mcp/ToshMcpServer.cs); `suggest_command` is still open.
- Structured error output mode for machine-consumable diagnostics

### Documentation
- Language reference: formalize the existing LaTeX spec into a living reference AI agents can query
- Expand `AGENTS.md` as the language and shell evolve

### Project Memory
- Create a persistent / scalable project memory storage that can be used by any AI Companion

---


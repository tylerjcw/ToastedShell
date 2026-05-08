# TōSh Backlog

Open work items by area, roughly ordered by priority within each section.
Completed items prior to 2026-05-07 live in
[BACKLOG-archive.md](BACKLOG-archive.md).

Last updated: May 7, 2026. Lambda return-type annotations, postfix
`if`/`unless` on `return`/`break`/`continue`/`throw`/`yield`, lazy
parenthesised generator comprehensions `(body <| for ...)`, the rune
base set, and a backlog audit against the actual implementation all
landed in this pass. Earlier additions: Line Editor Phase 1, user-defined
error types, top-level signal flow fix, `Tosh.Compiler` IR + IL emitter
spike, streaming display sinks, `iterate`/`recur` builders, VS Code
extension, MCP `run_snippet`/`explain_error` tools.

Recent additions: First-Class .NET Citizenship section (Waves 1–3) reflecting
the 2026-05-06 audit; spec restructured with new `\part{Compilation}`. See
[FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) and
[SPEC_STATUS.md](SPEC_STATUS.md) for the full audit/roadmap pairing.

---

# REPL Line Editor ✓ Phase 1 Complete

Derived from [Line Editor RFC](LINE_EDITOR_RFC.md). Phase 1 is focused on safety and deterministic editing.

## Phase 1 Checklist

- [x] Preserve multiline draft while navigating wrapped lines
- [x] Fix continuation indentation growth in multiline editing
- [x] Add foundational undo/redo support in `LineEditorBuffer`
- [x] Add edit transaction grouping (coalesced typing, completion-accept as single transaction)
- [x] Add explicit draft snapshot/restore model around history traversal
- [x] Add multiline history traversal modifiers (`Alt+Up` / `Alt+Down`) without draft clobber
- [x] Add focused tests for key behavior matrix in multiline + completion contexts

### Priority: P0 — Complete

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

# First-Class .NET Citizenship

Derived from the 2026-05-06 codebase audit. Companion to
[FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) (full roadmap of
14 items) and [SPEC_STATUS.md](SPEC_STATUS.md) Gap §10. The waves below are
the execution order; each task lands with conformance rows and a doc update.

## Wave 1 — "a tosh DLL feels like a .NET DLL"

Reassessed 2026-05-06 with reflection probes against `--compile` output:

- **Async `Task<T>` for user funcs (originally Wave 1 #1):** *Not a gap.*
  Typed funcs already emit as sync `T`-returning CLR methods
  ([BoundUnitEmitter.cs:4892](../src/Tosh.Compiler/BoundUnitEmitter.cs#L4892)).
  There is no `async func` surface syntax, and reflection on a probe DLL
  shows ordinary sync signatures (`add(Int32, Int32) -> Int32`). The audit
  finding was a misread of the pipeline-stage code path. **De-scoped.**
- **Single typed CLR method per func (originally Wave 1 #2):** *Already
  done.* Reflection on an overloaded probe (`func pick(a: int)` and
  `func pick(a: int, b: int)`) shows exactly two typed methods, no
  `object`-shaped peer shim. **De-scoped.**
- **Metadata-only reference assemblies (Wave 1 #3):** *Still real.* This
  is the only remaining Wave 1 item.

### Metadata-only reference assemblies

`--emit-refasm` already stamps `[ReferenceAssembly]` but ships fat method
bodies. Strip bodies in the refasm pass so `.ref.dll` is a real contract
surface (prerequisite for ABI v1 work and library NuGet packaging).

- [x] Replace body-bearing emit with metadata-only emit in the refasm pass at `BoundUnitEmitter.cs:697`.
- [x] Verify metadata parity between implementation and refasm via reflection diff test.
- [x] Conformance: C# direct compile against refasm; runtime resolution against the implementation DLL. _2026-05-07: validated via the A3 cross-language smoke test — a C# console with `<PackageReference Include="GreeterLib" />` compiled against `ref/net10.0/GreeterLib.dll` and at runtime resolved `lib/net10.0/GreeterLib.dll`, calling the tosh-defined `greet("C# consumer")` and printing `Hello, C# consumer!`._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 8.
- Priority: P0.

## Wave 2 — "ship to NuGet"

The distribution gap. Today only `Tosh.Sdk` is packable; user libraries
have no NuGet path.

### Standalone library NuGet packages

- [x] Set `IsPackable=true` and pack metadata for `Tosh.Runtime`. _2026-05-07: shared metadata centralised in `Directory.Build.props`; nupkg lands in `artifacts/packages/`._
- [x] Set `IsPackable=true` and pack metadata for `Tosh.Compiler.Runtime`. _2026-05-07: ProjectReferences (`Tosh.Runtime`, `Tosh.Stdlib`, `Tosh.Language`) correctly serialise as NuGet `<dependency>` entries — also packed transitively._
- [x] Validate restore from a clean machine. _2026-05-07: validated end-to-end via the A2 `dotnet new tosh-lib`/`tosh-app` smoke test — a fresh project pointed at `artifacts/packages/` as a NuGet feed restored Tosh.Sdk + Tosh.Runtime + Tosh.Stdlib + Tosh.Language + Tosh.Compiler.Runtime + Tosh.Tui and produced a working DLL/apphost._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 9 (audit follow-up).
- Priority: P1.

### `dotnet new` templates

- [x] `dotnet new tosh-lib` template (library). _2026-05-07: ships in `Tosh.Templates`; defaults to `<OutputType>Library</OutputType>` with `ToshEmitReferenceAssembly=true`._
- [x] `dotnet new tosh-app` template (executable). _2026-05-07: ships in `Tosh.Templates`; defaults to `<OutputType>Exe</OutputType>` with apphost._
- [x] Smoke test: `dotnet new tosh-lib && dotnet build && dotnet pack` succeeds. _2026-05-07: lib builds to `MyLib.dll` + `MyLib.ref.dll`, app builds and runs (`Hello from a tosh-app!`). `dotnet pack` for `.toshproj` is A3 work and tracked there._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 9 (audit follow-up).
- Priority: P1.

### `dotnet pack` for `.toshproj`

- [x] Wire a `<ToshPack>` / `dotnet pack` flow in `Tosh.Sdk` so user-authored `.toshproj` libraries produce a NuGet consumable from C#. _2026-05-07: `Tosh.Sdk` now defaults `IsPackable=true` for `OutputType=Library` and emits a wrapper csproj under `obj/<cfg>/<tfm>/pack-wrapper/` that invokes `Microsoft.NET.Sdk` Pack to produce a nupkg with `lib/<tfm>/<asm>.dll`, `ref/<tfm>/<asm>.dll`, and transitive `<dependency>` entries for `Tosh.Runtime` / `Tosh.Stdlib` / `Tosh.Language` / `Tosh.Compiler.Runtime`. Forwards user `<PackageReference>` items, stamps `ToshRuntimeVersion` into the packed `Sdk.props`._
- [x] Cross-language smoke test: pack a tosh library, reference it from a C# project via `<PackageReference>`, call into it. _2026-05-07: `dotnet new tosh-lib -n GreeterLib && dotnet pack` produced `GreeterLib.1.0.0.nupkg`; a separate C# console added it via `dotnet add package GreeterLib`, restored Tosh.* runtime deps from the local feed, and successfully invoked `Greeter.greet` and the top-level `greet` over reflection — matching the conformance row above._
- Depends on Wave 1 #3 (real refasm).
- Priority: P1.

### Reproducibility, SourceLink, symbol packages

- [x] Turn on `Deterministic`, `ContinuousIntegrationBuild`, `EmbedUntrackedSources` in `Directory.Build.props`. _2026-05-07: `Deterministic=true` always, `ContinuousIntegrationBuild=true` when any of `CI`/`TF_BUILD`/`GITHUB_ACTIONS` is set, `EmbedUntrackedSources=true` always, `PublishRepositoryUrl=true` so the nuspec carries the commit-pinned `<repository url=… commit=…>`._
- [x] Wire SourceLink (`Microsoft.SourceLink.GitHub` or equivalent) across all `Tosh.*` projects. _2026-05-07: `Microsoft.SourceLink.GitHub` 8.0.0 added as a global build-only `<PackageReference>` in `Directory.Build.props`. `GitRepositoryRemoteName=github` overrides the default `origin` (which is a private host on dev machines). Verified: PDBs now contain `{"documents":{"/home/komrad/projects/tosh/*":"https://raw.githubusercontent.com/.../<commit>/*"}}`._
- [x] Decide strong-naming policy (sign with project SNK, or explicitly opt out and document). _2026-05-07: deliberately **not** strong-named. Documented in the `Directory.Build.props` block — strong naming would force every consumer (tosh-lib NuGets, plugins) to either sign too or `InternalsVisibleTo` dance around it, with no meaningful security benefit for an OSS shell. Revisit if a Windows/Defender or GAC requirement ever appears._
- [x] Emit `.snupkg` symbol packages alongside implementation packages. _2026-05-07: `IncludeSymbols=true; SymbolPackageFormat=snupkg` plumbed via `Directory.Build.targets` (so `<IsPackable>` is settled before evaluation). `dotnet pack Tosh.slnx` now produces 5 `.snupkg` files (one per C# library) next to the 7 `.nupkg`s. `Tosh.Sdk` and `Tosh.Templates` are content-only and explicitly opt out._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 9 (audit follow-up).
- Priority: P2.

## Wave 3 — "tooling parity"

### LSP capability gaps

The binder and symbol resolution already support these; the LSP just needs
to advertise and route them.

- [x] `textDocument/references` (find all references). _2026-05-06: scope-aware via `DeclarationIndex.FindReferences`; covers variables, function overloads, classes/modules/enums/records; respects shadowing._
- [x] `textDocument/rename` + `textDocument/prepareRename`. _2026-05-06: `BuildRenameEdits` returns a `WorkspaceEdit`; `PrepareRename` returns the editable range at the cursor (strips leading `$` for variable refs)._
- [ ] `textDocument/formatting` and `textDocument/rangeFormatting` wired to a deterministic tosh formatter. _Deferred — see **Source Formatter** below._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 12 (audit follow-up).
- Priority: P1.

### Source Formatter

A deterministic source-code formatter for `.tosh` files. **Phase 1
shipped 2026-05-07.** Lives at
[`src/Tosh.Language/Formatting/ToshFormatter.cs`](../src/Tosh.Language/Formatting/ToshFormatter.cs);
exposed as the `format` builtin
([`src/Tosh.Language/Bridge/Scripting/FormatCommand.cs`](../src/Tosh.Language/Bridge/Scripting/FormatCommand.cs)).

#### Phase 1 — shipped

- [x] Pure round-trip: re-renders top-level structure
      (statement separators, indentation, blank lines, brace placement,
      keyword spacing) and uses original-source slices for inner
      expressions and unsupported statement kinds, so output is
      always valid.
- [x] Style: 4-space indent, single blank line between top-level
      declarations, opening braces same line, no trailing semicolons.
- [x] Idempotent: `format(format(x)) == format(x)` (verified by
      `Format_is_idempotent` test).
- [x] Coverage: `var`/`const`/`export`/`global`/`shy` declarations,
      pipelines, `return`/`yield`/`break`/`continue`/`throw`,
      `if`/`else`, `for`, `while`, `func` definitions; brace-delimited
      decls (class/enum/record/struct/interface/trait/union/module/rune)
      slice through to the matching `}` to work around a parser-side
      span quirk.
- [x] CLI: `format <path>` (rewrite in place), `format --check <path>`
      (non-zero exit if any file would change), `format --stdout <path>`,
      `format --diff <path>`, `format -` (read stdin).
- [x] Tests: [`tests/Tosh.Tests/FormatterTests.cs`](../tests/Tosh.Tests/FormatterTests.cs)
      (9 cases — var, func, if/else, blank-line separators, class
      closing-brace, idempotency, parse-error fallback, trailing
      newline, postfix conditionals).

#### Phase 2 — open

- [ ] Real-AST formatting for inner expressions (drops the
      source-slice fallback) so spacing inside arithmetic,
      member-access, function calls, etc. is normalised.
- [x] Comment preservation. Lexer captures every `#` line comment
      (full-line + trailing) into a parallel `LineComment` list
      surfaced via `ParseResult.LineComments`; the formatter flushes
      pending full-line comments before each statement (preserving
      blank-line gaps between groups) and re-attaches trailing
      same-line comments to the line they came from. Works inside
      block bodies via the `WriteStatement` flush hook.
- [x] Structural coverage for `try`/`catch`/`finally`, `switch`/`case`,
      and variable/member assignments (`$x = expr`, `$x += expr`,
      `$obj.field = expr`). `match` expressions and lambda bodies
      still take the source-slice path for now.
- [x] Wire LSP `textDocument/formatting` and `textDocument/rangeFormatting`
      (range currently delegates to whole-document formatting) plus
      `documentFormattingProvider` / `documentRangeFormattingProvider`
      capabilities.
- [ ] `match` arms and lambda bodies — currently slice-fallback;
      promote to AST emit so nested decls reformat consistently.
- Priority: P2.

### XML doc comments (CLR-visible documentation)

Tosh already keeps `##` lines as `DocComment` tokens with structured
`@param`/`@returns`/`@example`/`@throws`/`@see`/`@since`/`@deprecated`
tags. Make them surface to other .NET languages by emitting an ECMA-334
sidecar `<assembly>.xml` next to the compiled `.dll` so Roslyn, Rider,
IntelliSense and DocFX pick them up the same way they would for a
C#-authored library.

No new tosh syntax is required — `## <summary>...</summary>` already
parses today because the lexer keeps the post-`## ` text verbatim. The
work is on the emit + parsing-shape side.

- [ ] Extend [`DocComment`](../src/Tosh.Language/Parsing/DocComment.cs)
      to also capture **raw XML pass-through** lines (lines that begin
      with `<` after stripping the `## `) into a new
      `IReadOnlyList<string> XmlBlocks` member. Keep the existing
      `@`-tag parsing for ergonomic authoring; both can coexist on a
      single declaration.
- [ ] Auto-translate `@`-tags to standard XML on emit:
      `Description` → `<summary>`, `@param=name desc` →
      `<param name="name">desc</param>`, `@returns` → `<returns>`,
      `@example` → `<example><code>…</code></example>`, `@throws T msg`
      → `<exception cref="T">msg</exception>`, `@see ref` →
      `<seealso cref="ref"/>`, `@since v` → `<remarks>Since v.</remarks>`.
      `@deprecated` is already a CLR concern — keep emitting
      `[ObsoleteAttribute]` and additionally translate the message into
      a `<remarks>` block.
- [ ] New `XmlDocWriter` next to
      [`BoundUnitEmitter`](../src/Tosh.Compiler/BoundUnitEmitter.cs)
      that walks types/methods/properties/fields/events as they are
      defined and accumulates `<member name="…">…</member>` entries
      keyed by ECMA-334 doc-IDs (`T:Ns.Type`,
      `M:Ns.Type.Method(System.Int32)` with mangled parameter type
      names, `P:`, `F:`, `E:`). Generic arity uses the ECMA backtick
      form (`` `1 ``).
- [ ] Wire writer flush into
      [`ToshPublisher`](../src/Tosh.Compiler/ToshPublisher.cs) so the
      `.xml` lands beside the `.dll` in `bin/<config>/<tfm>/` and is
      copied to the publish output and the ref-asm package layout
      (`lib/<tfm>/Foo.xml` + `ref/<tfm>/Foo.xml` for nupkg).
- [ ] Stamp `<doc><assembly><name>{asm}</name></assembly>` header.
      Honour CLR's "no XML comment" warning suppression — emit only
      `<members>` entries for declarations that actually had `##`.
- [ ] Tests:
      1. Parse-level: `## <summary>desc</summary>` + `## @param=x foo`
         on the same func produces both an `XmlBlocks` entry and a
         `Parameters[x]` entry without losing either.
      2. Emit-level: compile a `library`-profile script with `func
         add(a: int, b: int) -> int` carrying `## adds two numbers`
         and `## @param=a first` and `## @returns sum`; assert the
         emitted `.xml` has `<member
         name="M:…add(System.Int32,System.Int32)">` containing
         `<summary>` + `<param name="a">` + `<returns>`.
      3. Roundtrip: a C# consumer hovers the tosh-emitted method and
         Roslyn surfaces the summary (xunit + Roslyn workspace).
- [ ] Doc-ID generator must respect mangling rules from `CLR_ABI_v1`
      (Tosh-original-name → CLR name) so the IDs match the methods
      Roslyn sees, not the source-language identifiers.
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md).
- Priority: P2 — nice ergonomics for downstream .NET consumers, no
  blocker for ABI v1.

### CLR ABI v1 spec document

Lock the public rules once Waves 1–2 produce a stable shape. This is the
"we promise not to break this" artefact downstream consumers need.

- [x] Draft ABI v1 covering: assembly identity, type/method naming and mangling, visibility mapping, overload rules, library vs executable mode, attribute set (`ToshOriginalNameAttribute`, `ToshTypeAttribute`), nullability/refinements/dynamic erasure rules. _DONE 2026-05-07. Spec lives at [`docs/CLR_ABI_v1.md`](CLR_ABI_v1.md), normative + frozen at v1.0. Emitter changes: `guarded`→`Family`, `local`→`Assembly` on fields & methods; `[assembly: ToshAbi(1)]` stamp; `ParameterAttributes.HasDefault` + `SetConstant` on typed params with literal defaults; `[ParamArrayAttribute]` on rest params with array CLR type._
- [x] ABI test set: reflection, Roslyn C# compile against refasm, `ProjectReference`, `PackageReference`, runtime `ToshHost` invocation. _DONE 2026-05-07 via the GreeterLib cross-language pack+consume smoke test (Wave 2 above): C# consumer with `<PackageReference Include="GreeterLib" />` compiles against refasm and runs against impl. Reflection / `ProjectReference` paths are exercised in the existing test suite (2315/2315 pass)._
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 5.
- Priority: P2.

### `--profile=library` alias

Promote `runtime` to the official redistributable-library contract: alias
`--profile=library` to `runtime` plus typed-public-signature enforcement
plus metadata-only refasm. Document `permissive`-compiled assemblies as
*executable bundles*, not libraries.

- [ ] Add the alias in `CliInvocationResolver`.
- [ ] Add an SDK property `<ToshLibraryMode>true</ToshLibraryMode>` shorthand.
- [ ] Spec update: extend `\part{Compilation}` with a "library mode" sub-section.
- Tracks: [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 3 (audit follow-up).
- Priority: P2.

## Deferred (post-wave)

- **Tier-3 reduction** ([FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 4). High-leverage long-term, high-cost short-term. Revisit after Waves 1–2.
- **Rune model decision** ([FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) item 13). Research-shaped; defer.
- **Native interop expansion** beyond primitives (struct marshalling, callbacks, `Span<T>`, `[MarshalAs]`). Defer until a real user need surfaces.

---

## Completed

Historical "✓ Shipped" entries through 2026-05-07 have been moved to
[BACKLOG-archive.md](BACKLOG-archive.md) to keep this file focused on
open work. The archive preserves the full text of each completed item
(macros, generics, comprehensions, slicing, IL emitter spike, streaming
display sinks, VS Code extension, MCP tools, AOT performance findings,
etc.).


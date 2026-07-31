# TōSh Backlog Archive

Historical record of completed backlog items, extracted from
[BACKLOG.md](BACKLOG.md) on 2026-05-07. Items here are kept verbatim as
shipped — refer back to [BACKLOG.md](BACKLOG.md) for the live worklist
and to [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) /
[SPEC_STATUS.md](SPEC_STATUS.md) for the post-wave audit pairing.

---

## Completed

### Macro system (runes) ✓

Tosh ships a full Boo-style AST-macro system under the name **runes**.
Definitions use the `rune` keyword and receive their arguments as
unevaluated `RuneThunk` objects so the macro can splice the body into a
new AST shape before evaluation. Implementation:
[ToshParser.ParseRuneDefinitionStatement](../src/Tosh.Language/Parsing/ToshParser.cs#L2420),
[`RuneCommand`](../src/Tosh.Language/Bridge/RuneCommand.cs),
[`RuneDefinition`](../src/Tosh.Language/RuneDefinition.cs),
[`RuneThunk`](../src/Tosh.Language/RuneThunk.cs).
Rune-level modifiers (`sealed`, `leaky`, `fixed`, `lazy`) are parsed
today. Built-in runes (`dbg`, `unless`, `benchmark`, `with-retry`) live
in [BuiltinRunes.cs](../src/Tosh.Language/BuiltinRunes.cs). `unless`
works as `unless $failed (echo "ok")`.

### Class & type system: generics, function overloading, operator overloading ✓

- **Generics**: `class Stack<T>` and `type Pair<A, B> = …` parse via
  `ParseTypeParameterList` ([ToshParser.cs:4637](../src/Tosh.Language/Parsing/ToshParser.cs#L4637))
  and execute end-to-end. Type arguments are resolved into
  per-instance bindings on `ToshClassInstance` (keyed by class
  type-parameter name); the engine substitutes them at every type-name
  use site (property reads/writes, constructor parameters, method
  parameters, method return types). Bindings are propagated through
  the inheritance chain via
  `ToshClassDefinition.BaseTypeArgumentsResolved`, so
  `class Foo<T> extends Base<T>` and `class Foo extends Base<int>`
  both resolve correctly inside ancestor members. User-defined
  generic class annotations such as `Box<int, string>` are recognised
  by `ToshEngine.IsKnownAnnotatedType` (no need to fall through to the
  CLR type loader, which would choke on multi-arg names). Strict
  no-coercion enforcement applies whenever a parameter or property
  type was originally a class type-parameter — e.g.
  `new Box<string>(42)` rejects with
  `tosh.runtime.annotation_conversion_failed` rather than silently
  stringifying. Compiled mode threads type arguments through a new
  `ToshHost.NewObject(typeName, bareTypeName, string[] typeArgs, object?[] args)`
  overload, and generic class declarations skip CLR-shell emission
  (registering via source replay) so the engine can reify them at
  runtime. Tests live in [GenericClassTests.cs](../tests/Tosh.Tests/GenericClassTests.cs).
- **Function overloading**: same-name `func` declarations with distinct
  arities or typed signatures are merged into an
  `OverloadedFunctionCommand` ([src/Tosh.Language/Bridge/OverloadedFunctionCommand.cs](../src/Tosh.Language/Bridge/OverloadedFunctionCommand.cs))
  via `ToshEngine.TryMergeFunctionOverload`. Class methods/constructors
  use the same overload resolution path in `ToshClassDefinition`.
- **Operator overloading hook**: `IsOverloadableOperatorToken`
  ([ToshParser.cs:4903](../src/Tosh.Language/Parsing/ToshParser.cs#L4903))
  recognises `+`, `-`, `*`, `/`, `[]`, etc. as legal class-method names.

### Slice / bracket indexing ✓

`$list[1:3]`, `$str[:-1]`, `$arr[::2]`, and `$dict[$k]` all work today.
The pipeline form (`... | get 2..5`) accepts a `ToshRange` and is
implemented in [GetCommand.cs](../src/Tosh.Stdlib/Pipeline/GetCommand.cs#L40).

### Comprehensions: Cartesian, parallel/zip, tuple destructuring ✓

```tosh
[($x + $y) <| for x in [1,2], y in [10,20]]       # Cartesian
[$x + $y <| for x in [1,2,3] || y in [10,20,30]]  # parallel/zip
[$a + $b <| for (a, b) in [(1,2),(3,4)]]          # destructuring
```

All three forms emit through the comprehension lowering path in
`ToshParser` (`innerIsParallel`, multi-source `for`, tuple-pattern
binders). Only true lazy generator expressions remain open.

### `Tosh.Compiler` IR + IL emitter spike (toshc) ✓

A walking-skeleton `Tosh.Compiler` now lowers a usable subset of tosh
straight to .NET IL via `Reflection.Emit`. Bound IR carving covers C-1
(try / throw / return / match / switch), C-2 (types), C-3 (declarations
and niche literals/expressions), closures (Block / Lambda /
CallableInvocation), and control flow (If / For / While / Until / Break /
Continue / Conditional / IfExpression). On top of that the emitter
handles variables, arithmetic, string interpolation, if/while/assignment,
user-defined function definitions and calls, for-range loops + compound
assignment, object-typed numeric ops, member access, list/dict literals,
foreach over iterables, and a `ToshHost` runtime bridge that routes
non-echo commands through the interpreter for parity. Multi-stage
pipelines land in three phases: phase 1 (commands only), phase 2 (block
arguments), phase 3 (full pipelines). All wired through `--compile` and
covered by the existing test suite.

### Streaming display sinks ✓

The REPL no longer materializes long-running pipelines before rendering.
`IDisplaySink` abstracts row delivery; `BufferingDisplaySink` keeps the
classic "compute widths from all rows, then emit" path,
`StreamingTableSink` writes an append-only bordered table as rows arrive,
and `AutoDisplaySink` decides between them based on TTY-ness, the
profile's `StreamingHint`, and a first-row latency threshold. The CLI
consumes `engine.ExecuteAsync(...)` directly via `await foreach`.
Bottom borders are drawn from `try/finally` so Ctrl-C never leaves a
half-open table.

### `iterate` / `recur` infinite sequence builders ✓

Two new Functional pipeline commands cover the "stateful unfold" niche:
`iterate <seed> <callable>` produces `[seed, f(seed), f(f(seed)), …]`
and `recur (a, b, …) <callable>` produces values from a multi-seed
recurrence relation (the callable receives the last N values). Both are
infinite — pair with `first`, `take-while`, or `take-until` to bound.
The full `lazy [...]` self-referential list syntax (Haskell-style) is
still tracked in the open backlog.

### MCP tools `run_snippet` and `explain_error` ✓

`Tosh.Mcp.ToshMcpServer` now exposes two new agent-callable tools:
`run_snippet` evaluates a tosh fragment in an isolated engine and
returns structured stdout / stderr / diagnostics, and `explain_error`
takes a `tosh.*` diagnostic code and returns its category, severity,
suggested fix, and source-of-truth file. `suggest_command` remains
open.

### VS Code language extension ✓

`editor/vscode/tosh.tosh-lang` ships a TextMate grammar with full
syntax highlighting, bracket matching, comment toggling, snippet
integration, and language-configuration for `.tosh` files. Pairs with
the existing LSP and MCP servers for end-to-end editor support.

### User-defined error types (`class FooError extends Error`) ✓

Tosh classes can now extend `Tosh.Runtime.ToshError` (surfaced inside tosh
as `Error`) to define typed, throwable error hierarchies that round-trip
between interpreter, compiled mode, and C# consumers.

- **`Tosh.Runtime.ToshError : Exception`**: unsealed base for user error
  types; carries `Message`, `TextSpan Span` (auto-stamped at the throw
  site), and `object? Cause`.
- **`Error` alias**: registered in `DotNetTypeResolver.Aliases` so
  `extends Error` and `Error` references resolve without a fully-qualified
  name. `Tosh.Runtime` is now in `DefaultImplicitUsings`.
- **Throw boundary normalization**: `RaiseThrownValue(span, value)` in
  `ToshEngine` (and mirroring `ToshHost.ThrowValue` for compiled mode)
  raises `Exception` instances verbatim (preserving CLR type identity for
  cross-language `catch (HttpError)`), wraps `ToshClassInstance` whose
  definition's `ClrBaseType` derives from `Exception` into a real
  `ToshError` (so the user's tosh class crosses the boundary as a CLR
  exception with the original instance available via `.Cause`), and
  wraps non-Exception values in `ThrowSignalException` exactly as before.
  All paths stamp `Data["tosh.thrown"] = true`.
- **Catch round-trip**: `CreateCaughtErrorValue` (interpreter) and
  `ToshHost.CaughtValueOf` (compiled) unwrap the `ToshError` →
  `ToshClassInstance` bridge so inside `catch (err) { … }` the user sees
  their original instance — pattern checks (`$err is FooError`) and
  property access (`$err.Status`) work uniformly.
- **IL catch widening**: compiled-mode `EmitTryStatement` now opens with
  `BeginCatchBlock(typeof(Exception))` and routes through `CaughtValueOf`,
  which rethrows control-flow signals (`Return`/`Break`/`Continue`) so
  user catch blocks cannot swallow them.
- **Diagnostic codes**: uncaught user errors surface as
  `tosh.user.<TypeName>` (e.g. `tosh.user.HttpError`); plain throws
  (`throw "boom"`, `throw 42`) keep `tosh.runtime.throw`. Re-throws
  preserve the original diagnostic and append a `re-thrown at <site>`
  info line.
- **ABI**: `docs/CLR_ABI_v1.md` §9 rewritten with both throw shapes, the
  `Data["tosh.thrown"]` marker, and the recommended `extends Error`
  pattern with C# consumer example.

### Top-level signal flow fix ✓

Three latent bugs in `EvaluateParseResultAsync` were causing top-level
`return`, `break`, `continue`, and synchronous user-throws to be silently
swallowed when they occurred before `MoveNextAsync` was reached:

- **`return` at script top level lost its values.** A subcommand body
  ending in `return $hs` produced no output because the
  `ReturnSignalException` catch added values to a local list but never
  yielded them. Fixed by capturing `pendingReturnValues` and flushing
  them via `yield return` after the outer try/finally.
- **`break` / `continue` outside loops bubbled raw signals.** Because
  some signal-throwing statements (`break`, `continue`, synchronous
  `return`/`throw`) raise during `GetAsyncEnumerator()` rather than
  `MoveNextAsync()`, the original catch arms never fired. Fixed by
  wrapping enumerator creation in its own catch block that mirrors the
  `MoveNextAsync` arms.
- **Cleared three long-standing test failures**:
  `Return_exits_top_level_scripts_early`,
  `Auto_sourced_tosh_shebang_scripts_without_extension_can_be_used_in_subexpressions`,
  `Break_and_continue_outside_loops_raise_diagnostics`. Also unblocked
  user-facing scripts using subcommand `return` patterns (e.g.
  `headset info` from `~/.local/bin/headset`).

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

Full comprehension syntax with `<|` operator. All four collection types: `[body <| for x in source]` (list), `{: body <| for x in source :}` (set), `{% key => value <| for x in source %}` (dict), `(body <| for x in source)` (generator). Supports `where` filtering, `let` bindings, and nested `for` clauses. 14 tests.

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

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


---

# Absorbed from BACKLOG.md, 2026-08-16

BACKLOG.md was dissolved into [the plan](plan/README.md). Its live work was filed as
items; everything below is the closed or superseded remainder, kept verbatim.

| Was | Now |
|---|---|
| External-Program I/O Compact — M5 deferred | `TOSH-0005` |
| Crumb Polish | `CRUMB-0001` |
| Native callbacks / function pointers | `TOAST-0011` |
| `Span<T>`/`Memory<T>` native shapes | `TOAST-0012` |
| Test-suite parallel flakiness | `PLAN-0002` |
| Language Features & Paradigms (planned) | `docs/IDEAS.md` |
| First-Class .NET Citizenship waves | frozen with the compiler |

The .NET waves are not filed as items. They are the execution order for the compiled
backend, which the separation plan freezes out of the solution; see COMPILED_TOSH.md
and FIRST_CLASS_DOTNET_STATUS.md.

---

# TōSh Backlog

Open work items by area, roughly ordered by priority within each section.
Completed items prior to 2026-05-07 live in
[BACKLOG-archive.md](BACKLOG-archive.md).

> **Active language stabilization:** The prioritized ToastScript repair
> program, semantic decisions, and acceptance gates live in
> [the plan](plan/README.md). Update that
> document rather than duplicating its item statuses here.

> **Status, July 30, 2026 — feature backlog, frozen; not re-audited.**
> This document tracks *features*. It is not the source of truth for language
> semantics and has not been re-audited since May 7, 2026, while roughly half the
> project's commit history landed after that date. Treat closed items as history
> and open items as candidates rather than as a current plan.
>
> Thirteen items are open, all of them features rather than defects: LSP
> `textDocument/formatting` and the source formatter, doc-comment XML emission,
> and library mode. **Three are gated** by the July 30 priority decision that
> compiled ToastScript is an experiment until the interpreted language is solid
> (see [ROADMAP.md](ROADMAP.md)) — library mode's SDK property, the
> `\part{Compilation}` spec section, and doc-ID mangling per
> [CLR_ABI_v1.md](CLR_ABI_v1.md).
>
> Defects, semantic decisions, and the acceptance gates live in
> [the plan](plan/README.md). Nothing here
> should be started before that programme's P1 tier is closed.

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

# External-Program I/O Compact (2026-05-13) — P0

**Status:** M1–M4 landed 2026-05-13. M5 (polish) deferred. See
[TSSP.md §11](TSSP.md#11-producing-tssp-from-net-toshclient) for the
`Tosh.Client` surface; [examples/tsspdemo](../examples/tsspdemo) is
the canonical second-consumer demo.

Building first-party tools (`crumb`, future helpers) for daily use as
login-shell children exposed a sharp gap: there is no clean,
documented way for an external program to render structured output to
TōSh *and* read interactive input from the user at the same time.

The forcing example: `crumb -S <many AUR pkgs>` at a bare TōSh prompt.
Crumb was on the `KnownStructuredCommands` allowlist in
[ExternalProcessCommand.cs](../src/Tosh.Stdlib/ExternalProcessCommand.cs),
which forces the piped path so TōSh can parse TSSP frames. That path
also redirects stdin and never calls `tcsetpgrp`, so the child opens
`/dev/tty` but TōSh's REPL still owns the controlling terminal — every
keystroke is contended. Symptom: prompts that accept no input.

Immediate mitigation: the allowlist is empty. Interactive children
take the full passthrough path (`tcsetpgrp` → foreground group →
inherited stdio). TSSP rendering for `crumb` at a bare prompt is
suspended until the proper plumbing lands.

### Goals

1. **Hybrid passthrough mode** — pipe stdout *only*, inherit stdin and
   stderr, hand off the foreground process group, parse TSSP framing
   from the piped stdout while child I/O on `/dev/tty` works normally.
2. **A documented client contract** that external programs can adopt:
   - Negotiation envvars (`TOSH_STRUCTURED_STDOUT`, `TOSH_STDOUT_CONSUMER`,
     `TOSH_TSSP_VERSION`, `TOSH_STDIN_ACCEPTS`, color/width hints,
     `TOSH_TTY`, `TOSH_STDIO_MODE`).
   - Where to write human status (stderr / `/dev/tty`).
   - Where to write structured data (stdout, TSSP frames only).
   - Where to read input (always `/dev/tty`, with `TCIFLUSH` drain).
   - Job-control expectations (child becomes group leader, parent
     `tcsetpgrp`s the child, child handles SIGINT/SIGTSTP/SIGQUIT).
3. **A reusable client library** so we stop reimplementing this per app:
   - C#: `Tosh.Client` package (`src/Tosh.Client`, `net10.0`, zero deps
     on the rest of the tree). TSSP frame writer, status/prompt helpers
     backed by `/dev/tty`, env-var negotiation, color detection.
     Replaces `Tosh.Crumb/Output/{Confirm,Tty,TtyRedirect}.cs`.
   - Shipped as both a ProjectReference (in-tree) and a NuGet package.
   - Eventually mirror libraries for other languages.
4. **Worked example + docs** wired into `docs/ARCHITECTURE.md` and a
   new `docs/EXTERNAL_PROGRAMS.md` so anyone building a tool for
   TōSh has one canonical reference.

### Milestones (locked 2026-05-13)

**M1 — Hybrid spawn mode in ToSh.** ✅ Landed 2026-05-13.
`ExecuteWithHybridAsync` in
[ExternalProcessCommand.cs](../src/Tosh.Stdlib/ExternalProcessCommand.cs):
stdout piped (TSSP parser), stdin/stderr inherited, child placed in its
own pgrp with `TrySetForegroundGroup`, `WaitForForegroundChild` for full
Ctrl-C/Z/D job control. Opt-in via `$tosh.Config.External.HybridConsumers`
(default seeded with `crumb`). `ApplyTsspEnvironment` adds
`TOSH_TTY` and `TOSH_STDIO_MODE`. Frame parser is liberal: non-TSSP
bytes on hybrid stdout are echoed verbatim with a one-time
`tosh: tssp.unframed_output` stderr warning.

**M2 — `Tosh.Client` library.** ✅ Landed 2026-05-13.
New `src/Tosh.Client` project. `ToshHost.Current` exposes `Info`,
`Status` (/dev/tty-first), `Prompt` (/dev/tty + TCIFLUSH per call),
`OpenFrameWriter(schema)` returning a thread-safe `ToshFrameWriter`.
`ChildTtyScope.Acquire()` provides a dup/dup2 fd-swap for child
spawns that should drive the terminal directly.

**M3 — Crumb migration.** ✅ Landed 2026-05-13.
Deleted `src/Tosh.Crumb/Output/{Tty,TtyRedirect}.cs`. `Confirm.cs`
rewritten as an 8-line shim over `ToshHost.Current`. `PackageFormatter`
uses `ToshFrameWriter` (`CrumbTsspMetaFrameTests` passes — wire-
compatible). 4 `TtyRedirect.Acquire()` call sites repointed to
`Tosh.Client.ChildTtyScope`. End-to-end smoke under hybrid spawn:
`crumb list --explicit | first 3` renders the full 30-column table.

**M4 — Docs + second consumer.** ✅ Landed 2026-05-13.
[docs/TSSP.md §11](TSSP.md#11-producing-tssp-from-net-toshclient)
documents the `Tosh.Client` surface and hybrid-spawn opt-in.
[examples/tsspdemo](../examples/tsspdemo) is a 30-line second consumer
proving the contract isn't crumb-specific.

**M5 — Polish (deferred).** Real `crumb.install-plan` schema with
box-drawing TōSh display profile. Optional: TSSP `progress` frame
routed to a Tome progress bar. Optional: probe-based auto-discovery
for hybrid-capable binaries when config lists are inconvenient.
Line-buffered forwarding of unframed hybrid output.

### Acceptance

- Crumb's `Output/` directory replaced with `Tosh.Client` calls.
- `crumb -S` at a bare ToSh prompt: prompts work, status streams live,
  install-plan summary renders as a TōSh-styled table (TSSP frame).
- `crumb -Ss dotnet | from json` still produces structured records.
- Smoke test: a second tiny consumer demonstrates the contract end-to-end.

### Non-Goals

- A full pty multiplexer or terminal-emulator layer.
- Forcing every external command to participate — programs that
  ignore the envvars must keep working exactly like they do today.

---

# Crumb (Pacman + AUR Helper) Polish — 2026-05-13 — P2

After the M1–M4 TSSP work plus the upgrade/install/removal UX pass
(boxed colorized tables, summary matrix, group expansion, quiet
makepkg by default), the project review surfaced the items below as
the next batch of polish. None block daily use; ordered roughly by
leverage.

## Resolved quick wins

- **Crumb coverage exists.** Focused tests now live in
  [tests/Tosh.Tests/Crumb*.cs](../tests/Tosh.Tests/), covering
  pacman-style flag expansion, option parsing (including `--limit`),
  formatter/TSSP selection, privilege probing, and version comparison.
- **Colour detection is centralized.**
  [ColorSupport.cs](../src/Tosh.Crumb/Output/ColorSupport.cs) owns
  stdout/status colour gating and truecolor detection; formatters route
  through it.
- **Startup validates cache prerequisites.**
  [Program.cs](../src/Tosh.Crumb/Program.cs) now rejects an environment
  with neither `$HOME` nor `$XDG_CACHE_HOME` before AUR/cache paths are
  touched.
- **`--limit N` shipped.** `CrumbOptions.Parse` accepts `--limit` and
  `--limit=N`; search/news commands trim results accordingly.

## P2 — medium features

- **Honest stub handling.** ✅ Landed 2026-05-13. `-Sw` now maps to
  `install --download-only`, using `pacman -Sw` for repo targets and
  fetching AUR PKGBUILDs without building. `-Suw` / `-Syuw` download
  pending repo upgrades. `-U <file...>` maps to `install-file` and
  delegates to `pacman -U`.
- **Split `UpdateAsync` and `InstallAsync` into phase methods.** ✅
  Landed 2026-05-13. Install now separates validation/planning,
  rendering/confirmation, repo execution, and AUR fetch/build phases.
  Update now separates repo upgrade/download, AUR discovery, review,
  download-only, and rebuild phases.
- **`crumb logs` subcommand.** ✅ Landed 2026-05-13. `crumb logs`
  lists newest build logs from `$XDG_CACHE_HOME/crumb/log/`; supports
  `--pkg <name>`, `--tail`, `--clean`, `--limit N`, and `--dry-run`.
- **Config file** (`~/.config/crumb/crumb.toml`). Today everything is
  env vars (`CRUMB_SUDO`, `CRUMB_PAGER`, `CRUMB_REVIEW`,
  `CRUMB_NO_TRUECOLOR`, `CRUMB_NO_COLOR`, `TOSH_TTY`). A TOML config
  with env-var overrides would let users persist build flags, default
  `--quiet`/`--verbose`, an exclude list for `-Syu` (e.g. skip
  `*-git`), pager, and truecolor preference.
- **Improved conflict-resolution UX.** `DependencyResolver` already
  detects conflicts; the prompt is binary proceed/abort. Show which
  installed package the conflict is with, and offer granular
  remove-or-skip for each.

## P3 — larger / optional

- **Pacnew/pacsave detection** after install — paru-style.
- **Downgrade support** via the Arch archive.
- **`--aur-base-url`** env var / config for testing against mock or
  mirror AUR endpoints (`AurClient` already accepts the constructor
  arg; just no CLI wiring).
- **Document implicit behaviour**: pager precedence
  (`pagerOverride` > `CRUMB_PAGER` > `PAGER` > `less`), pacman-flag
  expansion semantics, format-flag last-wins.

### Acceptance

- P1 batch landed before the next user-visible feature pass.
- Long methods in `CrumbCommands.Update.cs` /
  `CrumbCommands.Install.cs` either split or annotated with phase
  comments — whichever serves clarity better.
- `crumb --help` lists no commands that throw `not implemented`.

### Non-Goals

- A mirror ranker (pacman owns mirrors).
- An alpm FFI binding (the on-disk DB parser is sufficient).
- Repo management (`crumb` is a client, not an admin tool).

---



A holistic project review surfaced seven priorities for the next quarter,
ordered by leverage. They cluster into three themes: **closing language
gaps that force user boilerplate** (#1), **lowering the onboarding tax for
polyglot developers** (#2, #7), and **tightening project identity** (#3,
#4, #5, #6). Items #1, #2, and #7 landed on 2026-05-08.

## 1. Numeric Generics / Trait-Like Constraints — P1 — closed (2026-05-08)

The current generic system has no equivalent of C# 11 static-abstract
interface members (`INumber<T>`, `IAdditionOperators<T,U,R>`), F# inline
+ SRTP, or Rust trait bounds. The forcing example is
[examples/point.tosh](../examples/point.tosh) — a generic `Point2D<T1, T2>`
must enumerate `+`/`-`/`*`/`/` overloads four times (one per right-hand
operand type) because the language cannot say "T must support `+`".

### Goals

- Express constraints like `where T: Add` / `where T: INumber` so a single
  `func +(other: T)` covers every numeric `T`.
- At minimum, recognise the four CLR static-abstract numeric interfaces
  (`IAdditionOperators<,,>`, `ISubtractionOperators<,,>`, `IMultiply…`,
  `IDivision…`) and surface them as built-in shorthand (`Numeric`,
  `Addable`, etc.).
- Extend the binder to verify operator-arithmetic statements against the
  declared bound at parse-time, not at value-flow time.
- Reduce the `point.tosh` body to a single overload set per operator.

### Non-goals

- Full Haskell-style typeclass system.
- User-defined trait declarations on top of CLR interfaces (defer until a
  pattern emerges).

### Priority: P1 — **closed (2026-05-08)**

Initial implementation:
- Parser accepts `where T: <Constraint>[, <Constraint>…]` clauses after
  the class header (multiple `where` clauses allowed).
- Built-in constraint registry (`Numeric`/`Number`/`INumber`,
  `Add`/`Sub`/`Mul`/`Div`, `Comparable`, `Eq`) — see
  [src/Tosh.Language/ToshTypeParameterConstraintRegistry.cs](../src/Tosh.Language/ToshTypeParameterConstraintRegistry.cs).
- Validation runs at instantiation; violations throw a structured
  diagnostic citing the failing constraint.
- Unknown constraint names are accepted conservatively (reserved for
  user-defined trait constraints in a future pass).
- Followups: surface constraints in LSP hover, propagate to operator
  dispatch type-checking, allow user-defined constraints.

### Phase 1.x / Phase 2 update — 2026-05-09

Generics evolved past the original "trait-like constraints" goal into a
fuller C#-style system. Landed in this round:

- **Phase 1.2** — `type-of` on a generic instance returns a
  `BoundGenericTypeDescriptor` whose `Name` / `FullName` /
  `IsGenericType` / `TypeArguments` reflect the bound substitution
  (e.g. `Point2D<Int32>`). See
  [src/Tosh.Language/BoundGenericTypeDescriptor.cs](../src/Tosh.Language/BoundGenericTypeDescriptor.cs).
- **Phase 1.3** — User-defined constraints. A `where T: SomeName`
  whose name is not in the built-in registry now resolves through
  `ToshEngine.TryResolveTypeName` and accepts any CLR type assignable
  to it (so `where T: IDisposable` works without a built-in entry).
  See `ToshClassDefinition.TrySatisfyUserConstraint`.
- **Phase 2.1** — `func name<T>(...)` / `func map<T,U>(...)` parse and
  execute. `EraseTypeParameter` recursively strips type-parameter
  names from nested generic annotations (`list<T>` → `list`).
- **Phase 2.2** — Per-call inference. `BindFunctionParameters` returns
  an inferred-type table; `ApplyGenericBinding` records the first
  binding and strict-validates later parameters. Mismatch raises
  `tosh.runtime.generic_argument_type_mismatch`.
- **Phase 2.3** — `where T: …` clauses on free functions; reuses the
  built-in registry plus the CLR-interface fallback.
- **Phase 2 deferred** — explicit call-site type args (`box<int> 42`)
  are blocked on parser disambiguation: `<` is overloaded for input
  redirection. Plan: input redirection is always followed by `(`
  (`<( … )`), so `foo<X>` with a non-`(` next token is unambiguously
  a generic call. Capture in Phase 3.3 below.

### Phase 3 — Inference depth & call-site polish — P1

Next round, in priority order:

1. **Nested-shape inference** (`func first<T>(items: list<T>) -> T`).
   ✓ DONE (2026-05-09) — annotation walker unifies
   element / key / value types into the per-call binding table; nested
   `dict<K, list<V>>` etc. work. Inference now runs *before*
   `ConvertFunctionParameterValue` so element types aren't widened.
2. **Return-type contribution.** ✓ DONE (2026-05-09).
   When `T` only appears in the return type and the call site has a
   target type (`var x: int = identity<T> 42`), the LHS annotation
   propagates through an `AsyncLocal<string?>` set at the
   variable-declaration boundary, stamped onto `CommandInvocation`,
   and unified annotation-vs-annotation against the function's
   `RawReturnTypeName` to seed the per-call binding table. Nested
   shapes (`var xs: list<int> = wrap 42`) work via recursive
   head/arg matching.
3. **Explicit call-site type args.** ✓ DONE (2026-05-09).
   Disambiguation is trivial because the lexer already emits a single
   `<(` token for input redirection — a bare `<` immediately after a
   command name (no whitespace) is unambiguously a generic argument
   list. Inferred-binding table is seeded from the parsed type-args
   before parameter conversion. Operator-detection lookahead skips
   over generic-arg lists at depth 0 to avoid mis-parsing
   `foo<int> 1 2` as a comparison.
4. **Generic methods on classes.** ✓ DONE (2026-05-09).
   Parser, type-parameter erasure (combined class+method scope), and
   class-method invocation all carry the method's `TypeParameters` /
   `TypeParameterConstraints`. `ToshClassDefinition.ExecuteMethodBlock`
   now constructs a synthetic `CommandContext` from the method's
   source info + parameter spans and calls
   `ToshEngine.InferMethodTypeBindings` to populate a method-scoped
   binding table, which is merged with any class-level bindings
   carried by the instance. Strict per-call validation fires when
   different arguments imply different bindings for the same `U`,
   matching the diagnostic shape used for free functions.

### Phase 4 — Constraint richness — P2

5. **Recursive / parameterized constraints**
   (`where T: IComparable<T>`). ✓ DONE (2026-05-09).
   Parser now consumes `<…>` after the constraint bareword via
   `ParseTypeNameSuffix`, producing a constraint string like
   `IComparable<T>`. The runtime constraint check substitutes
   type-parameter references with their inferred bindings (the
   currently-binding T plus any other type parameters already in
   `typeBindings`) before resolving via `TryResolveTypeName`. Mixed
   bindings flow correctly: `IDictionary<K, V>` resolves with both
   parameters substituted.
6. **C#-style multiple constraints** (`where T: A, B`).
   ✓ DONE (2026-05-09). The parser already supported comma-separated
   constraints in a single clause, and multiple separate `where`
   clauses also work (`where A: Numeric where B: Comparable`).
   Each constraint name is checked independently in registration order.
7. **Special constraints** — `new()`, `class`, `struct`, `notnull`,
   `unmanaged`. ✓ DONE (2026-05-09). Added to
   `ToshTypeParameterConstraintRegistry`:
   - `new` / `new()` — public parameterless ctor (value types always pass).
   - `class` — non-value type (reference type / interface).
   - `struct` — non-nullable value type.
   - `notnull` — accept-all (CLR types are never null).
   - `unmanaged` — recursive predicate over fields.
   Parser passes `new` as a bareword constraint; the registry alias
   for `new()` covers users who write the C# form.
8. **`default(T)` expression.** *Deferred* — requires a new expression
   AST node, parser support for `default(TypeName)`, and pushing the
   per-call `typeBindings` table into a scope visible from the
   function body so `T` can resolve to its bound CLR type. Workaround:
   pass a default-valued argument explicitly, or use `null` for
   reference types.

### Phase 5 — Generics on other declarations — P2

9. ✓ DONE — Generic records (`record Pair<A,B>(first: A, second: B)`).
   - Parser: type-parameter list and `where` clauses (both pre- and
     post-field positions).
   - `ToshRecordDefinition` carries `TypeParameterNames` /
     `TypeParameterConstraints`; `CreateGenericInstance` validates
     constraints and builds bound instances. Field annotations matching
     a type-parameter name are strict-checked (`IsInstanceOfType`),
     mirroring class-parameter behavior.
   - Engine `new` dispatch handles records analogously to classes.
   - Structs / unions / enums deferred — out of scope until concrete use
     cases surface.
10. ✓ DONE — Generic interfaces (`interface IRepo<T>`) with substitution
    at `fulfills` check time.
    - Interface parser accepts `where` clauses; runtime carries
      `TypeParameterNames` / `TypeParameterConstraints`.
    - At `class … fulfills IRepo<int>` sites, `ValidateInterfaceTypeArguments`
      enforces arity, rejects bare references to generic interfaces, and
      validates concrete type arguments against the interface's
      where-clauses. Type arguments that forward the implementing
      class's own type parameters are deferred (validated at
      instantiation).
    - New diagnostics: `tosh.runtime.missing_interface_type_arguments`,
      `tosh.runtime.unexpected_interface_type_arguments`,
      `tosh.runtime.interface_type_argument_arity_mismatch`,
      `tosh.runtime.interface_type_argument_constraint_violation`.
11. ✓ DONE — Type-alias transparency in the type checker.
    - Plain aliases (`type Id = int`) and refinement aliases (`type
      Positive = int where _ > 0`) both project to a `RefinementType`
      wrapper around the resolved base; the type checker now unwraps
      that wrapper inside `IsAssignable`, so alias names compare
      transparently to their bases without false `tosh.type.mismatch`
      diagnostics in script-mode.
    - Generic aliases (`type MyList<T> = list<T>`, `type Bounded<T> = T
      where _ > 0`) work at use sites by recursing through the alias's
      template base — leaning on `Dynamic`-element placeholders and the
      structural list/array/dict element-recursion now in `IsAssignable`.
      Precise structural substitution of type parameters is a separate
      follow-up.
    - List-literal `IList` source compatibility: a raw list literal
      (currently lowered as `BoundType.FromClr(typeof(IList))`) now
      flows freely into any `ListType` / `ArrayType` slot and likewise
      for raw dictionaries.
    - `EnsureRefinementAliasNameDoesNotConflictWithType` no longer
      consults the wide CLR-resolver fallback, fixing spurious
      `tosh.runtime.type_name_conflict` errors on alias names like
      `Pair` that happen to collide with arbitrary loaded-assembly
      types. Conflicts now only fire against user-declared named types.
    - Tests: 6 new cases in `tests/Tosh.Tests/TypeCheckerTests.cs`
      lock in alias transparency for plain, refinement, generic-
      refinement, parameterized-base, and forwarding-generic aliases,
      plus a negative case ensuring real mismatches still report.

### Phase 5 — Followups still open

- ✓ DONE — Precise structural substitution of generic-alias type
  parameters. `TypeNameResolver.ResolveGeneric` now detects when a
  user-type template is a `RefinementType` carrying a
  `TypeAliasStatementSyntax` with declared type parameters, validates
  arity, overlays each `T -> arg` mapping into a child resolver, and
  re-resolves the alias's `BaseTypeName`. The result is a precise
  `RefinementType(substitutedBase, "MyList<int>", alias)` instead of
  the previous `Dynamic`-erased `GenericInstanceType` wrap.
  Diagnostic emitted on arity mismatch and on type arguments applied
  to a non-generic alias. 4 new tests in `TypeCheckerTests.cs` cover
  int/string substitution, two-parameter aliases, and arity errors.

### Phase 6 — Advanced features — P3

12. ✓ **Variance (`out T` / `in T`).** *Done 2026-05-09.* The
    parser recognises optional `out` / `in` prefixes inside a
    type-parameter list and threads them through
    `InterfaceDefinitionStatementSyntax.TypeParameterVariances`,
    `ToshInterfaceDefinition.TypeParameterVariances`, and the
    `UserInterfaceType` registry entry. `TypeChecker.IsAssignable`
    now consults the per-parameter variance when comparing two
    `GenericInstanceType`s wrapping the same interface template:
    covariant slots use one-way `IsAssignable(fromArg, toArg)`,
    contravariant slots flip it, invariant slots require
    bidirectional assignability. Variance is honored only for
    interface templates — classes/records/structs stay invariant,
    matching C#. 4 new tests in `TypeCheckerTests.cs` cover
    covariant widening, invariant rejection of widening,
    contravariant flow in reverse, and covariant rejection of
    narrowing.
13. *Skipped for now — reflection builtins.* `is-generic-type`,
    `type-arguments`, `generic-definition`, `make-generic-type` would
    be cheap to add but pile onto the ~209-builtin surface that
    section 3 below already flags as needing pruning. Revisit after
    the command-audit pass settles which families consolidate.
14. **Compiler-emit (`tosh --compile`) lowering of generic call
    sites.** Largest effort. The interpreter path is the source of
    truth; `BoundUnitEmitter` currently bails on generic-instance
    member access and constrained dispatch. Needs parallel work for
    type-param-keyed locals, generic-method dispatch, and runtime
    `IsInstanceOfType` checks at member boundaries.
15. **Constraint expressiveness — user-interface constraints.** ✓ DONE
    `where T: ISomeUserInterface` is now enforced at generic-class
    instantiation: `ToshClassDefinition.TrySatisfyUserConstraint` looks
    up the constraint name as a `ToshInterfaceDefinition` and walks the
    bound type-arg's `ToshClassDefinition.ImplementedInterfaces` chain
    (including base classes) for membership. Inherited interfaces from
    a parent class satisfy the constraint. Built-in registry constraints
    (Numeric, Comparable, op_Add, …) and CLR interface constraints
    (`IDisposable`, etc.) continue to work as before. Truly unknown
    constraint names remain conservatively accepted. Records and
    interfaces still accept user-interface constraints conservatively
    — mirroring the new class behavior is a small follow-up.
16. **Type inference at call sites.** ✓ DONE
    Ctor-position inference now binds type parameters from the
    runtime types of `new ClassName(args)` arguments — both the
    bare-T case (`class Box<T>(initial: T)` ⇒ `T = int` for
    `new Box(42)`) and nested annotations (`class Box<T>(values:
    list<T>)` peeks the list's element type). Unified via a small
    recursive `UnifyCtorAnnotationWithValue` that handles
    list/array/dict shapes and any generic CLR type with matching
    arity. Applies to both classes and records. Constraint
    validation still fires after inference, so `new Box("hi")` on
    a `where T: Numeric` class is rejected. Method-call inference
    on instance / static methods remains explicit-`<T>`-only and
    is a follow-up.

---

## 2. Standard-Name Aliases for Class Modifiers — P1 — closed (2026-05-08)

The flavored modifier set (`shy`, `proud`, `guarded`, `vital`, `overrule`,
`hollow`, `hermit`, `fading`, `fixed`) renames concepts that already have
universal industry names. Every C#/Java/Swift/Kotlin/TypeScript developer
must learn a translation layer with no semantic payoff, and LLM tooling
mis-suggests TōSh code accordingly.

### Goals

- Accept these canonical aliases in the parser **without removing the
  flavored forms**:

  | Canonical (new) | Flavored (kept) |
  |-----------------|-----------------|
  | `private`       | `shy`           |
  | `public`        | `proud`         |
  | `protected`     | `guarded`       |
  | `required`      | `vital`         |
  | `override`      | `overrule`      |
  | `abstract`      | `hollow`        |
  | `static`        | `shared` (existing)/`hermit` (class) |
  | `readonly`      | `fixed`         |
  | `obsolete`      | `fading`        |

- Document canonical forms as the recommended style, with flavored forms
  preserved as synonyms.
- Update LSP completions, hover text, and AGENTS.md to lead with the
  canonical names; mention the flavored synonyms in a short table.
- Run a single pass over `examples/` to convert at least the headline
  examples (e.g. `examples/point.tosh`) to canonical names so search
  results land on canonical syntax.

### Non-goals

- Removing flavored forms (would break `examples/`, profile.tosh
  ecosystems, and stylistic charm).
- Changing IL emission for these modifiers — they already lower to the
  same CLR semantics.

### Priority: P1 — **closed (2026-05-08)**

- Parser accepts `private`, `public`, `protected`, `required`,
  `override`, `abstract`, `static`, `readonly`, `obsolete` as direct
  synonyms for the flavored modifiers (member-level), and `abstract` /
  `static` at the class level. See
  [src/Tosh.Language/Parsing/ToshParser.cs](../src/Tosh.Language/Parsing/ToshParser.cs).
- AGENTS.md modifier tables now lead with the canonical name.
- Followups: LSP completions/hover prefer canonical names; convert
  example sources opportunistically.

---

## 3. Surface-Area Pruning — P2 — audit complete + first wave landed (2026-05-10)

255 builtins is PowerShell-scale and growing. Several clusters duplicate
each other or expose unsafe primitives by default.

### Audit pass — DONE 2026-05-09

Full audit lives at [`docs/SURFACE_AUDIT.md`](SURFACE_AUDIT.md), driven
by the `--export-command-metadata` JSON dump (255 commands). Every
command is tagged **Keep / Fade / Move / Consolidate / Rename** with
rationale. Counts: 196 keep, 12 fade, 6 move, 30 consolidate, 11 rename.

### First-wave consolidation — DONE 2026-05-10

- **CLR verb-fade landed.** `call`, `call-method`, `get-prop`,
  `get-props`, `get-methods`, `set-prop`, `del-prop`, `has-prop`,
  `has-method` carry `[CommandDeprecated("26.05.0.10")]` with notes
  pointing at the canonical syntax (`$obj.Method($args)`, `$obj.Prop`,
  `$obj.Prop = value`, `members has X`, …).
- **`members` and `methods` got subcommands.** Both accept
  `has <name>` and `get <name>`. `members` additionally accepts
  `props` / `fields` / `methods` / `events` to slice by member kind.
  `props` and `funcs` are top-level shortcuts.
- **`get` is now the canonical column-picker** with variadic field
  projection (`get name size extra`). `select` and `pick` remain as
  soft aliases.
- **`row` is the new canonical row-picker** — variadic on indices,
  list literals, and ranges (`row 7 8 9`, `row [3,1,0]`, `row 1..3`).
  Bad indices throw `tosh.row.index_out_of_range`.

### Remaining action items

1. ~~**Gate native FFI behind `tosh-interop` module**~~ — _Won't do
   (2026-07-31)._ The OWASP A04 framing assumes an actor the threat model
   does not have: this is a single-user shell whose author is actively
   writing FFI, so the gate is friction with no attacker to stop. It is
   also now aimed at the wrong layer — after the fluent-interop work the
   capability lives in the *statements* (`raw struct`, `bind native`,
   `raw func`), not the six commands, which are vestigial for anything
   but dynamic sizes and raw pointer arithmetic. If gating ever becomes
   real (a multi-user or embedded host), gate the statements.
2. **Streaming/throughput contract** (item 6 below) — uses this audit
   as the authoritative command list. Tag each Pipeline command
   lazy/eager/short-circuiting in `help`. _Open._
3. **`prompt <segment>` subcommand consolidation** — spec migration
   path; keep `prompt-*` as fading aliases for one major. _Design-first._
4. **Alias-fade mechanism.** `RegisterAlias` has no "soft-deprecated"
   flag. Either extend the registry, or document the secondary aliases
   (`pick`, `select`, `foreach`, `avg`, `sort-by`, `stddev`, `summary`)
   as docs-only fading until a registry change lands. _Open._
5. **Pin canonical names for soft-alias rows** in AGENTS.md so
   completion + LLM tooling rank canonical first
   (`average` over `avg`, `each` over `foreach`, `get` over
   `pick`/`select`, `sort` over `sort-by`, `stdev` over `stddev`,
   `summarize` over `summary`, `forget` over `unset`). _Open — doc only._

### Priority: P2 — *first wave landed (2026-05-10); remaining items are mechanical or design-first*

---

## 4. Operator-Overload IL Emission Uses CLR Conventions — P2 — closed (2026-05-13)

`func +(other) { … }` currently lowers to a method named after the
symbol. CLR consumers (C#, F#, PowerShell) cannot resolve TōSh-defined
operators because they expect `op_Addition`, `op_Subtraction`, etc.

### Goals

- Emit both names (or emit only the CLR-canonical name and accept the
  symbolic form as syntax sugar that resolves to it).
- Verify a TōSh class's `+` is callable from a C# consumer in the
  `Tosh.Tests` cross-language sample.
- Map the full overloadable set: `+ - * / % == != < <= > >=` (and the
  corresponding `op_*` names; `=~`/`!~` and `**`/`//` need either custom
  attribute-tagged dispatch or a TōSh-specific calling convention).

### Priority: P2 — **closed (2026-05-13)**

- `ToClrOperatorName` helper in
  [src/Tosh.Compiler/BoundUnitEmitter.Functions.cs](../src/Tosh.Compiler/BoundUnitEmitter.Functions.cs)
  maps the full canonical set (`+` → `op_Addition`, `-` → `op_Subtraction`,
  `*` → `op_Multiply`, `/` → `op_Division`, `%` → `op_Modulus`, `==`,
  `!=`, `<`, `<=`, `>`, `>=`). Symbolic-only operators with no CLR
  convention (`**`, `//`, `=~`, `!~`) get stable `op_Tosh*` names.
- Applied at both `DefineMethod` sites in
  [BoundUnitEmitter.Classes.cs](../src/Tosh.Compiler/BoundUnitEmitter.Classes.cs)
  (abstract method stub + regular instance method).
- Regression coverage:
  [BoundUnitEmitterTests.Compiled_operator_overload_emits_clr_canonical_method_name](../tests/Tosh.Tests/BoundUnitEmitterTests.cs)
  asserts `op_Addition` lands on the emitted type.

### Follow-up (not blocking closure)

TōSh operator methods are instance methods with `HasThis`, so a C#
consumer sees `box.op_Addition(other)` rather than `box + other`. Native
C# `+` syntax additionally requires the method to be `public static`
with both operands as parameters — a future change can synthesise a
static wrapper that forwards to the instance method.

---

## 5. Identity Statement in README — P2

The README sells three things at once: interactive shell, scripting
language, compiled-program target. Most example traffic
(`scripts/build.tosh`, `~/.local/bin/headset`, profile autoload modules)
suggests **scripting is the dominant identity**. Pick one (or rank
them) so future feature decisions have a north star.

### Goals

- Write a single-paragraph "what is TōSh" lede that ranks the three
  identities and explains the pitch in one sentence.
- Reorder the README's feature highlights to match.
- Consider stripping the compiled-program pitch from the headline if
  Wave 2 of First-Class .NET Citizenship isn't shipping in this cycle.

### Priority: P2

---

## 6. Streaming/Throughput Contract — P2 — closed (2026-05-13)

`first N` short-circuits today, but there's no documented contract that
says so. Users (and our own renderer optimisations) need a written
guarantee about which builtins are lazy, which are eager, and which
require materialisation.

### Goals

- Document each pipeline builtin's behaviour: **lazy** (`where`, `each`,
  `map`, `filter`, `take`/`first`, `skip`, `flatmap`), **eager**
  (`sort`, `sort-by`, `reverse`, `group-by`, `summarize`, `to json`,
  `count` when consuming the whole stream), **partial** (`first N`
  short-circuits; `last N` still drains).
- Add focused tests for each lazy builtin proving short-circuit
  behaviour against an infinite generator.
- Surface the contract in `help` topics (a one-line "Streaming: lazy /
  eager / short-circuiting" field on each builtin).

### Priority: P2 — **closed (2026-05-13)**

- `StreamingBehavior` enum (`Lazy` / `Eager` / `ShortCircuit`) + a
  class-level `[CommandStreaming(...)]` attribute live in
  [src/Tosh.Runtime/CommandStreamingAttribute.cs](../src/Tosh.Runtime/CommandStreamingAttribute.cs);
  reflected into `CommandMetadata.Streaming` by
  [ShellCommand.cs](../src/Tosh.Runtime/ShellCommand.cs) with a
  humanised form ("lazy" / "eager" / "short-circuit").
- ~56 pipeline commands under `src/Tosh.Stdlib/Pipeline/*` are tagged.
- `help` topics render a `Stream:` line inside the Pipeline sub-box
  ([HelpTopicSummaryRenderer.cs](../src/Tosh.Runtime/HelpTopicSummaryRenderer.cs))
  and the LSP markdown surface
  ([ToshLanguageFeatures.cs](../src/Tosh.LanguageServices/ToshLanguageFeatures.cs)).
- Short-circuit regression tests against an infinite generator land in
  [tests/Tosh.Tests/StreamingContractTests.cs](../tests/Tosh.Tests/StreamingContractTests.cs).

---

## 7. `$env.X = "value"` Assignment Sugar — P3 — closed (2026-05-08)

Documented as gotcha #1 in [AGENTS.md](../AGENTS.md). There is no
semantic reason `$env.X = "v"` should not desugar to `export X = "v"`;
the asymmetry exists because `$env` is currently a read-only namespace.

### Goals

- Recognise assignment to `$env.<name>` as sugar for `export <name> =
  <value>` at the binder/lowering stage; reject only on
  case/format-conflict edge cases that `export` itself rejects.
- Strip the gotcha from AGENTS.md and any other docs that warn about it.
- Add a regression test verifying both forms produce identical
  environment after execution.

### Priority: P3 — **closed (2026-05-08)**

- `ShellEnvironmentNamespace.TrySetMember` now routes through
  `ToshRuntime.ExportEnvironmentVariable`, the exact same path used by
  `export NAME = …`. See
  [src/Tosh.Runtime/ShellEnvironmentNamespace.cs](../src/Tosh.Runtime/ShellEnvironmentNamespace.cs).
- Case-insensitive lookup picks the canonical existing name, so
  `$env.path = "…"` updates `PATH`.
- AGENTS.md gotcha removed; both forms are documented as equivalent.

---



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
- ~~**Native interop expansion** beyond primitives~~ — mostly landed 2026-07-31
  (`raw struct`, inline arrays and char buffers, struct-by-value both
  directions, pointer-to-struct walking, `out`/`ref`, success contracts,
  errno). See the two follow-ups below for what remains.

- **Native callbacks / function pointers.** `func qsort(nint, nuint, nuint, callback)`
  is rejected — passing a TōSh closure where C wants a function pointer is the
  one interop shape still missing. Needs `Marshal.GetFunctionPointerForDelegate`
  over a closure, plus a lifetime story so the delegate is not collected while
  native code still holds the pointer (a rooted handle owned by the binding, or
  an explicit scope).
  - Blocks: sd-bus for `~/.config/tosh/lib/Bluetooth.tosh`, which currently
    re-spawns `bluetoothctl` on *every* property read — `$h.Name`,
    `$h.IsPaired`, `$h.Battery` is three processes. Also libarchive progress
    callbacks and any `qsort`-shaped API.
  - Priority: P2. _Open._

- **`Span<T>` / `Memory<T>` as native parameter shapes**, and explicit
  `[MarshalAs]` overrides for the cases the inference gets wrong.
  Priority: P3. _Open._

- **Test-suite parallel flakiness.** Single spurious failures appeared in 3 of 8
  consecutive full-suite runs on an unchanged tree, in two unrelated areas:
  `TtyCaptureTests.An_interpolation_hole_does_not_yet_capture` (shells out to
  `git` inside a PTY) and
  `GenericClassTests.Generic_class_user_interface_constraint_accepts_implementing_class`.
  Both pass reliably in isolation.
  - **Why it matters beyond noise:** a flaky suite makes bisection unsound. It
    already caused one incorrect attribution during the interop work, where a
    single green baseline was taken as proof that a change had caused a failure
    it had not.
  - Likely either shared process state (working directory, environment, PTY or
    file-descriptor pressure) or an xunit collection that should be serialised.
  - Priority: P2. _Open._

---

## Completed

Historical "✓ Shipped" entries through 2026-05-07 have been moved to
[BACKLOG-archive.md](BACKLOG-archive.md) to keep this file focused on
open work. The archive preserves the full text of each completed item
(macros, generics, comprehensions, slicing, IL emitter spike, streaming
display sinks, VS Code extension, MCP tools, AOT performance findings,
etc.).

# TōSh Backlog

Open work items by area, roughly ordered by priority within each section.

Last updated: April 17, 2026.

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

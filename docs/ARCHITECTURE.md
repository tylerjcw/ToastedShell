# TōSh Architecture

> **Reviewed July 30, 2026.** The Identity, Core Invariants, Runtime Value Model,
> and Display Model sections are current — they describe intent, and the intent
> has held. The factual sections (project structure, decisions, refactor plan)
> were rewritten in this pass after being last touched May 7, 2026, by which
> point roughly half the project's commit history had landed after them.
> Verified against the tree rather than from memory; where this document made a
> claim the code contradicts, the code won.

## Identity

`tosh` is not trying to be a clone of any single shell or language.

The target is:

- a Unix-inspired shell
- a lightweight scripting language with a Lua-like feel
- a `.NET` object runtime from top to bottom
- a Nu-style display system that renders objects beautifully without turning them into strings

In short:

> If NuShell and PowerShell had a child that got raised by ZSH and Lua.

That identity should guide both syntax and architecture.

## What TōSh Is

TōSh should feel:

- terse enough for interactive shell work
- dynamic by default
- optionally typed when the user wants stronger contracts
- comfortable for users coming from shells, scripting languages, or `.NET`
- object-first in the pipeline
- text-friendly at shell boundaries

TōSh should not feel like:

- C# script with pipes
- PowerShell with renamed keywords
- NuShell with `.NET` bolted on
- a Unix shell that serializes everything to text internally

## Core Invariants

These are the architectural rules we should optimize around.

### 1. The Pipeline Carries Real `.NET` Objects

Pipeline stages may transport many values, but each value is a real `.NET` object.

That means:

- `DateTimeOffset`, `TimeSpan`, `UnixFileMode`, `DriveInfo`, `FileInfo`, `DirectoryInfo`, `ExpandoObject`, `List<T>`, and other existing CLR types should flow through the shell unchanged when they are the best fit.
- Custom TōSh types should exist only when the CLR does not already model the domain well enough.
- Display should never redefine the underlying type.

The internal transport can remain stream-based, for example `IAsyncEnumerable<object?>`, as long as the values themselves stay real objects.

### 2. Objects First, Display Second

An object may have many views:

- scalar/compact
- record/detail
- table
- inspect/debug

Those are renderings, not different runtime values.

Examples:

- a `StorageSize` may display as `69.2 MB`
- a `DateTimeOffset` may display as `2 hours ago`
- a `UnixFileMode` may display as `rw-r--r--`

But the piped value must still be the typed object.

### 3. Text Is a Boundary Type, Not the Shell's Internal Truth

Text matters a lot at shell boundaries:

- user input
- external executables
- files
- networking
- templates

But once text is parsed or adapted, TōSh should prefer typed objects over stringly-typed data.

### 4. Dynamic by Default, Typed by Choice

TōSh should feel lightweight like Lua:

- fast to type
- forgiving in the REPL
- dynamic unless the user wants type annotations

Optional typing should improve clarity and tooling, not turn TōSh into C#.

## Language Direction

### Preferred Function Syntax

We should use a single canonical function form:

```tosh
func llf(directory: string) -> List<StorageFile> {
    ...
}
```

This is preferable to also allowing:

```tosh
List<StorageFile> llf(directory: string) {
    ...
}
```

Reasons:

- the `func` form is more shell-friendly and more Lua-like
- it avoids parser ambiguity around generics and command position
- it keeps the language visually distinct from C#

`func` should be the primary user-facing form.

### Parameters, Locals, and the Current Object

Recommended language conventions:

- `_` is the primary current pipeline object
- `$it` was retired; `_` is the only current-object symbol (`$it` now raises
  `tosh.runtime.unknown_variable`)
- parameters and locals should be referenced explicitly as `$name`
- declarations stay bare, for example `var name = ...` or `func work(name) { ... }`
- after declaration or parameter binding, variables should use `$name` consistently in the REPL, scripts, and blocks
- `$tosh.Last.Result` should expose the most recent successful statement result, while `_` remains item-scoped

Examples:

```tosh
func llf(directory) {
    ls -la $directory
    | where {
        _.Type == file
        _.Size >= 10mb
    }
}
```

### Return Semantics

We should distinguish between:

- emitting stream items
- returning a concrete collection object

Without explicit aggregation, a function should return the output of its last statement or pipeline as emitted values.

If the user wants one concrete collection object, that should be explicit:

```tosh
func llf(directory: string) -> StorageFile {
    ls -la $directory | where {
        _.Type == file
        _.Size >= 10mb
    }
}
```

This emits `StorageFile` objects.

```tosh
func llf(directory: string) -> List<StorageFile> {
    return collect {
        ls -la $directory | where {
            _.Type == file
            _.Size >= 10mb
        }
    }
}
```

This returns one `List<StorageFile>` object.

That distinction keeps stream semantics and collection semantics both understandable.

### Import Semantics

For clarity, we should eventually split:

- `using` for CLR namespaces and aliases
- `require` for TōSh files/modules

Even if TōSh temporarily supports file-based `using`, those are different concepts and will be easier to reason about if they diverge.

## Runtime Value Model

### Prefer Existing CLR Types

Use the CLR type directly when it already models the domain well:

- `DateTime`, `DateTimeOffset`
- `TimeSpan`
- `UnixFileMode`
- `FileInfo`, `DirectoryInfo`, `FileSystemInfo`
- `DriveInfo`
- `IPAddress`, `Uri`
- `ExpandoObject`
- `List<T>`, `Dictionary<TKey, TValue>`, `IReadOnlyList<T>`

### Use TōSh Types Only When Needed

Custom TōSh types are justified when the shell needs a stronger domain object than the CLR offers by default.

Good examples:

- `StorageSize`
- operation/result objects for side-effect commands
- diagnostics
- shell-specific projection/record objects if ordering or schema metadata matters

Custom TōSh types should remain easy to convert to native CLR types when there is a meaningful conversion path.

## User-Created and Parsed Objects

**Decided: TōSh has its own record type.** `{| a = 1 |}` produces a TōSh record
(`ToSh.record`), not an `ExpandoObject`. This document previously proposed
`ExpandoObject` as the default ad hoc object; the implementation went the other
way, for the reasons the same section listed as the conditions under which a TōSh
record would be justified — stable field ordering, schema metadata, and table/view
behaviour that `ExpandoObject` does not provide.

`ExpandoObject` remains a supported CLR type that flows through the pipeline
unchanged. It is simply not what the record literal produces.

The round-trip requirement still stands: records should convert cleanly to and
from `ExpandoObject`, `IDictionary<string, object?>`, and serialized formats such
as JSON.

## Display Model

The display system should follow these rules:

- `string` and shell text lines render raw
- a single object renders as a record/detail view
- a homogeneous collection renders as a table
- nested values render to a configurable depth
- terminal width controls truncation, column hiding, and wrapping
- errors and success/failure objects have dedicated display profiles

Success/failure objects are important.

Commands that only perform side effects should still return an object, but the renderer should be allowed to suppress trivial success objects by default so the shell stays readable.

That means the object still exists in the pipeline, but interactive display can stay quiet unless the user asks for more detail.

## Project Structure

Twenty projects build as part of `Tosh.slnx`. Sizes are C# source lines,
excluding `bin/` and `obj/`, as of September 25, 2026.

| Project | Lines | Responsibility |
|---|---:|---|
| `Tosh.Language` | 74,271 | Lexer, parser, binder, lowerer, type checker, evaluator (`ToshEngine`) |
| `Toast.Runtime` | 48,846 | Language runtime: value model, type system, operators, CLR interop, host contracts |
| `Tosh.Stdlib` | 31,474 | Shell half of the built-in command library |
| `Tosh.Tui` | 21,512 | Terminal UI primitives (rendering, layout, input) |
| `Tosh.Cli` | 20,732 | CLI entry point, REPL host, line editor, startup loader |
| `Tosh.Runtime` | 17,897 | Shell services: display engine, help catalog, jobs, session configuration |
| `Toast.Stdlib` | 14,461 | Language half of the built-in command library |
| `Tosh.Tome` | 9,939 | Tōme — terminal text editor, ships as its own binary |
| `Tosh.LanguageServices` | 7,900 | Shared analysis backend used by LSP and MCP |
| `Tosh.Crumb` | 5,381 | Crumb — pacman/AUR wrapper; tables to TTYs, NDJSON to pipes |
| `Tosh.DevCompanion` | 1,775 | MCP memory server (`tools/`), see [AGENTS.md](../AGENTS.md) |
| `Tosh.Mcp` | 838 | Model Context Protocol server for AI agents |
| `Tosh.Client` | 680 | TSSP frame writer and `/dev/tty` helpers; no dependency on the rest of the tree |
| `Tosh.Lsp` | 527 | Language Server Protocol server (self-contained binary) |
| `Tosh.Dap` | 524 | Debug Adapter Protocol server; dormant, exercised by `ProtocolSmokeTests` |
| `Tosh.ParityCheck` | 183 | Build-time advisory: command documentation and operator parity (`tools/`) |

Tests live in `tests/Tosh.Tests` (105,234 lines) and `tests/Tosh.LspFixture`.
Benchmarks live in `bench/`. Total tree: ~363,000 lines of C#.

There is no compiler. Scripts always run on the evaluator; single-file
distribution packs the runtime together with the script files
(`TOAST-ARCH-01`, [COMPILER_DECOMMISSIONING_PLAN.md](COMPILER_DECOMMISSIONING_PLAN.md)).

## Reference Codebases

These local repos are valuable reference points:

- TōSh: `/home/komrad/projects/tosh`
- NuShell: `/home/komrad/projects/nushell`
- PowerShell: `/home/komrad/projects/PowerShell`
- ZSH: `/home/komrad/projects/zsh`
- Lua: `/home/komrad/projects/lua`

We should reference each of them for different reasons.

### NuShell

Learn from NuShell:

- table rendering
- object exploration UX
- command discovery
- REPL polish
- data shaping ergonomics

Do not copy:

- the assumption that the pipeline's native value model is a Nushell-specific structured data type

### PowerShell

Learn from PowerShell:

- deep CLR interop
- command binding ideas
- object pipeline seriousness
- help/discovery patterns
- error and diagnostic richness

Do not copy:

- verbose cmdlet naming as TōSh's primary surface
- Windows-first assumptions

### ZSH

Learn from ZSH:

- shell feel
- completion quality
- globbing
- prompt experience
- interactive polish
- Unix expectations

Do not copy:

- text-only internal pipeline assumptions

### Lua

Learn from Lua:

- lightweight syntax
- dynamic-by-default philosophy
- compact scripting feel
- embeddable mindset

Do not copy:

- the absence of an object pipeline

## Locked Decisions

These were listed as pending in May 2026. All seven are settled and implemented;
they are architecture now, and changing one is a breaking change rather than a
course correction.

1. **`func` is the function keyword.** No return-type-prefix form exists.
2. **`_` is the current-object symbol.** `$it` was retired entirely.
3. **Typed functions are `func name(args) -> Type`.**
4. **Stream output and collection return are distinct**; `collect` makes
   aggregation explicit.
5. **`using` imports CLR namespaces; `require` imports TōSh files and modules.**
6. **Ad hoc records are a TōSh record type**, not `ExpandoObject` — decided
   opposite to this document's earlier proposal, see above.
7. **Display is a projection layer, never runtime truth.**

Two decisions have been added since:

8. **Paired collection delimiters** — `{` is block-only, `{| |}` is a record,
   `{% %}` a dict, `{: :}` a set (`TS-P2-25`).
9. **There is no compiler.** The evaluator is the only execution engine — see the
   standing decision in [ROADMAP.md](ROADMAP.md).

## Current Direction

The near-term refactor plan this document carried is complete or superseded:
`collect` shipped, stdlib is factored out of the runtime core, display is
separated from runtime concerns, and `Tosh.Core` — the "reorganize by
responsibility" target — is out of the build entirely.

Active work is the stabilization programme in
[the plan](plan/README.md), which is the source
of truth for defects, semantic decisions, and acceptance gates. Its governing
observation is worth repeating at the architecture level, because it is a design
lesson rather than a bug list: **the recurring root cause is two implementations
of one operation** — sync/async twins, two import paths, the constant folder
versus the operator evaluator, two equality paths. Where a second implementation
is unavoidable, it needs a guard that asserts the two agree. That is what the
standing drift guards in `tests/Tosh.Tests` exist for.

The known structural weaknesses, stated plainly so they are not rediscovered:

- `ToshEngine.cs` is 15,806 lines and `ToshParser.cs` is 12,219. Both are where
  duplicated implementations breed.
- Test coverage is deep in-process and thin at the edges — pty, terminal,
  protocol, editor. `TS-P1-30` survived 3,602 tests because a test process has no
  TTY, so every one of them exercised the branch that worked.

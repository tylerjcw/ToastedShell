# TōSh Architecture

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
- `$it` can remain as a compatibility alias
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

### `ExpandoObject`

`ExpandoObject` should likely become the default user-created ad hoc object type.

That makes sense for:

- objects built interactively in the REPL
- shell-created records
- parsed JSON objects
- parsed CSV rows

Why it is a good fit:

- it is a native CLR type
- it supports dynamic member access
- it also behaves like a dictionary
- it works naturally with reflection-friendly tooling

### When TōSh Still Needs Its Own Record Type

If TōSh needs:

- stable field ordering
- schema metadata
- provenance or projection metadata
- better table/view behavior than `ExpandoObject` alone provides

then a thin TōSh record type is still reasonable.

If we keep one, it should round-trip cleanly to and from:

- `ExpandoObject`
- `IDictionary<string, object?>`
- serialized formats like JSON

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

The repo is split into focused projects under `src/`:

| Project | Responsibility |
|---|---|
| `Tosh.Cli` | CLI entry point, REPL host, startup loader |
| `Tosh.Runtime` | Shared runtime types, attributes, value model, command metadata |
| `Tosh.Stdlib` | Built-in commands grouped by category (Filesystem, Text, System, …) |
| `Tosh.Language` | Lexer, parser, binder, lowerer, evaluator (`ToshEngine`), formatter |
| `Tosh.Compiler` | `tosh --compile` IL emitter (PersistedAssemblyBuilder) |
| `Tosh.Compiler.Runtime` | `ToshHost` shim that compiled assemblies link against |
| `Tosh.Sdk` / `Tosh.Sdk.Tasks` | MSBuild SDK + tasks driving `.toshproj` build/run/publish/pack |
| `Tosh.Templates` | `dotnet new tosh-app` / `tosh-lib` templates |
| `Tosh.LanguageServices` | Shared analysis backend used by LSP/MCP |
| `Tosh.Lsp` | Language Server Protocol server (self-contained binary) |
| `Tosh.Mcp` | Model Context Protocol server for AI agents |
| `Tosh.Dap` | Debug Adapter Protocol server |
| `Tosh.Tui` | Terminal UI runtime (widgets, frames, screens) |
| `Tosh.Core` | Legacy shim (`DisplayProfileRegistry`) — being phased out |

Tests live under `tests/Tosh.Tests` and `tests/Tosh.LspFixture`. Benchmarks
live under `bench/Tosh.Benchmarks`. The full solution is `Tosh.slnx`.

The earlier roadmap to split runtime / display / interop into their own
assemblies has largely landed: stdlib commands are factored out of the
runtime core, the language project owns its full pipeline (parse → bind →
lower → emit), and the compiler runtime is a separate assembly that
compiled artefacts link against rather than against the interpreter.

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

## Immediate Decisions To Lock

These are the next decisions we should treat as architecture, not experiments:

1. `func` becomes the primary function keyword.
2. `_` becomes the primary current-object symbol.
3. Typed functions use `func name(args) -> Type`, not return-type-prefix syntax.
4. Stream output and collection return are distinct; collection return should be explicit.
5. `using` is for CLR imports; `require` handles TōSh files.
6. `ExpandoObject` should be the default ad hoc record object unless a stronger TōSh-specific record type is justified.
7. The display system remains a projection layer, never the runtime truth.

## Near-Term Refactor Plan

1. Write down the language surface we want before adding more syntax.
2. Reorganize `TōSh.Core` by responsibility.
3. Separate display concerns from runtime concerns more cleanly.
4. Formalize side-effect result objects.
5. Add explicit collection aggregation semantics like `collect`.
6. Decide whether projections should stay custom or move toward `ExpandoObject`-first records.
7. Start reshaping help, completion, and the REPL around the new architecture rather than around individual commands.

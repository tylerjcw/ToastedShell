# Tosh Roadmap

For the current implementation snapshot and near-term priorities, see [STATUS.md](STATUS.md). This document stays focused on longer-term direction and design intent.

## Vision

`tosh` should grow into a shell, REPL, and scripting language with these end goals:

- Nu-inspired pipeline syntax that feels natural for shell work.
- A first-class `.NET` object runtime from top to bottom.
- A best-in-class interactive environment for exploring, transforming, and inspecting data.
- Rich, typed pipeline values that preserve meaning instead of collapsing into display strings.
- A display system that makes those values pleasant to read in a terminal without changing what they are.

In short: commands should return semantic `.NET` objects, the shell should render those objects beautifully, and the language should make them easy to compose.

## Guiding Principles

### 1. Objects First, Display Second

Pipeline output should always be the best available object model for the domain.

Examples:

- File permissions should be a native `UnixFileMode` value when available, not a raw `"drwxr-xr-x"` string.
- File sizes should ideally be a dedicated type or at least a richer numeric wrapper with units/helpers, not just presentation text.
- File modification times should stay as `DateTime` or `DateTimeOffset` values.
- Exceptions, processes, paths, and diagnostics should have shell-friendly object models with meaningful members.

Display is a projection over those objects. A user might see:

- permissions as `-rw-r--r--`
- a modified time as `2 days ago`
- a file size as `69.2 MB`

but the piped value should still be a typed object, not a string.

### 2. One Object, Many Views

The same object may need multiple textual representations:

- table view
- compact scalar view
- detail view
- inspect/debug view

Those are presentation choices, not different runtime values.

### 3. Consistency Matters More Than Cleverness

Objects of the same type should render the same way across commands unless the user explicitly chooses another view.

### 4. The REPL Is a Product

Before `tosh` becomes a large language, it needs to become a great place to think. Editing, completion, diagnostics, paging, tables, and object inspection are core features, not extras.

## Architectural Direction

### A. Typed Value Layer

We should gradually replace display-oriented properties with semantic types.

Near-term targets:

- native `UnixFileMode` instead of `Mode : string`
- `FileKind` or similar instead of free-form file type strings
- better path/value wrappers where plain strings are currently overloaded
- shell-specific diagnostic objects where appropriate

This does not mean every primitive becomes a wrapper type. It means values that carry domain meaning should stay meaningful in the pipeline.

### B. Display Profile System

We should build a proper display profile system on top of the runtime values.

The display layer should answer questions like:

- Which columns should this type show in a table?
- In what order?
- What is the default table/detail view for this type?
- How should a property be rendered in a cell?
- How can users override the rendering of a type or property?

Examples:

- `UnixFileMode` renders as `drwxr-xr-x` in tables, but remains queryable as a typed object.
- `DateTimeOffset` renders as `14 minutes ago` in compact table mode, but can be configured to render as ISO-8601 or Unix time.
- file sizes can render as `36.8 kB` while still being sortable/filterable numerically.

### C. User Customization

Longer-term, users should be able to define or override display behavior without changing the underlying objects.

Examples:

- set the default date rendering mode
- set per-type display profiles
- set per-property formatting rules
- choose between human-readable and exact/raw views

This should eventually live in shell configuration, profiles, or modules.

## Roadmap Phases

### Phase 1: Object Model and Display Foundations — DONE

The typed value layer is in place. Runtime objects stay semantic throughout the pipeline and display is a projection over them.

What shipped:

- native `UnixFileMode`, `FileAttributes`, and `FileSystemInfo` rendering
- `FileSystemEntry` carries domain values, not display strings
- display profiles for 40+ CLR types including `DateTime`, `DateTimeOffset`, `Exception`, `Process`, `Regex`, `Uri`, `IPAddress`, and more
- cell formatters distinct from raw property values
- raw/table/detail/compact display modes with user-configurable rendering for dates, sizes, permissions, and file attributes
- `view columns <type> ...` for user-defined per-type table column overrides

### Phase 2: REPL Quality — DONE

The REPL is the primary development surface.

What shipped:

- tab completion for commands, members, paths, types, modules, and CLR types
- multiline editing with bracket/quote/pipeline awareness
- reverse history search
- syntax highlighting
- paging for large output
- live color themes for tables, diagnostics, prompt, completion, and TUI
- dropdown-style completion picker
- ghost text completions

### Phase 3: Diagnostics and Observability — DONE

Structured diagnostics are in place across the parser and runtime.

What shipped:

- pointed, actionable errors with notes, secondary spans, and help messages
- command-specific diagnostics for common mistakes
- `inspect`, `members`, `describe-type`, and `constructors` for object exploration
- full-screen `help browse` with CLR namespace tree and topic search

### Phase 4: Language Core — DONE

ToSh is a real language, not just a command runner.

What shipped:

- variables, assignment, expressions, operators
- closures, blocks, `if`, loops, `match`, `switch`, ternary
- `func`, `class`, `enum`, `record`, `module`
- `try` / `catch` / `finally`, `throw`, `return`, `break`, `continue`
- `shy`, `global`, `export` visibility
- script files with `source` vs execution semantics

### Phase 5: External Command Integration — DONE

ToSh runs native processes and typed adapters side by side.

What shipped:

- native process invocation with stdout/stderr/exit-status capture
- explicit redirection (`out>`, `err>`, `o+e>`, `<<<`)
- background jobs with `&`, `jobs`, `wait-for`, `kill`, `signal`
- `exec` for process replacement
- typed Linux adapters for `ip addr`, `lsblk`, `findmnt`, `lscpu`, `lsfd`, `lsipc`
- `pipefail` policy
- globbing with `*`, `?`, `[]`, `**`, `@(...)`

### Phase 6: Modules, Profiles, and Extensibility — DONE

Users can extend and configure the shell.

What shipped:

- `module`, `using`, `require`, `export`, `shy`
- `config.tosh` → `profile.tosh` → `autoload/` startup chain
- runtime-backed `$tosh.Config` with `config` command for get/set/reset/init/browse
- live theme configuration for prompt, syntax highlighting, completion, diagnostics, tables, and TUI
- command discovery from loaded assemblies
- `view columns` for user-defined display profiles

### Phase 7: TUI Platform — DONE

A reusable terminal UI platform powers full-screen apps and inline prompts.

What shipped:

- `ITuiHost` / `ConsoleTuiHost` terminal abstraction
- `ITuiScreen` / `TuiApplication` app runtime with alternate-screen rendering
- reusable widget model: list, text, text-input, file-picker, option-picker, confirmation
- layout system: single, split-horizontal, split-vertical, stacked with configurable ratios
- `help browse` — full-screen help/API browser with CLR namespace tree
- `config browse` — full-screen config editor with live preview, validation, and staged edits
- `tui pick`, `tui filter`, `tui input`, `tui confirm`, `tui filepick`, `tui run` commands
- inline prompt rendering (`--cli` flag) with themed box-drawn tables, selection highlighting, search/filter, and multi-select
- `IInlinePromptProvider` abstraction for testable programmatic access
- 100+ TUI-specific tests

## Near-Term Direction

The active near-term work should continue to align with the core thesis:

1. Keep the shell object-first.
2. Prefer semantic runtime values over display-shaped strings.
3. Make interactive shell use pleasant enough that people want to stay in ToSh.
4. Harden the boring daily-driver details before chasing broad new surface area.

- the pipeline stays strongly typed
- the terminal stays pleasant to read
- the REPL becomes the primary development surface for the language

## What Still Separates ToSh From A Daily-Driver Shell

ToSh is no longer missing a single giant subsystem. The remaining gap is mostly breadth, hardening, and long-tail behavior.

### 1. Shell Semantics Hardening

These are the behaviors long-time shell users expect without thinking about them:

- predictable stdout/stderr redirection behavior
- predictable exit-status flow back to parent shells and calling scripts
- stronger native process composition, especially around pipes and errors
- richer globbing and path expansion
- coherent regex and text-pattern behavior across command surfaces
- more complete job/process edge-case handling

### 2. Native Interop Depth

ToSh already runs native commands well, but daily-driver shell use pushes harder on:

- process lifecycle control
- stream behavior under load
- path, quoting, and argument edge cases
- more typed adapters at the shell/text boundary

### 3. Display And Inspection Customization

The built-in display layer is strong, but a daily shell needs users to be able to shape it:

- per-type display profiles
- per-property column choices
- compact/table/detail/raw defaults per type
- config- or module-defined overrides

### 4. Testing And Weird-Case Coverage

Mature shells earn their size by surviving endless strange cases:

- mixed object/text pipelines
- partial failures
- unusual filesystem/process states
- nested script/module scope interactions
- interactive REPL editing edge cases

### 5. Performance And Startup Discipline

Users tolerate a lot less latency from a shell than from most apps:

- startup time
- memory footprint
- command dispatch overhead
- display/pager cost on large outputs

### 6. Packaging, Distribution, And Ecosystem

The core shell may be sound, but a daily-driver tool also needs:

- reliable publish/install/update stories
- more examples and batteries-included modules
- stable extension surfaces for users
- docs that answer real workflow questions quickly

### 7. Clear Compatibility Boundaries

ToSh does not need to be `sh`, `zsh`, NuShell, or PowerShell. It does need a clear story about where it fits:

- object-first shell, not POSIX-sh compatibility shell
- strong `.NET` interop, not every shell feature copied blindly
- great Unix/native interop where it materially improves the shell
- deliberate non-goals instead of accidental gaps

## Non-Goals Right Now

These can wait until the foundations above are in place:

- broad compatibility with every shell scripting edge case
- a large standard library
- package management
- advanced concurrency/distributed pipeline features
- full PowerShell compatibility

## Summary

If `tosh` is going to feel special, it will not be because it merely has Nu-like syntax or `.NET` interop.

It will feel special if:

- the objects are richer than typical shell values
- the rendering is better than typical object shells
- the REPL is excellent
- the language grows on top of those foundations instead of fighting them

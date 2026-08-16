# TōSh Roadmap

For open work items by area, see [BACKLOG.md](BACKLOG.md).

The active language-semantics program is tracked in
[the plan](plan/README.md). It is the
source of truth for interpreter/compiler convergence, parser hardening,
and the safety fixes opened by the July 2026 ToastScript review.

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

## Standing Priority Decision — July 30, 2026

**The interpreted language comes first. Compiled ToastScript is an experiment
until it is rock-solid.**

Compiled ToastScript ([COMPILED_TOSH.md](COMPILED_TOSH.md), `Tosh.Compiler` and
its IR and runtime bridge) is a working second implementation of the language's
semantics. That is what makes it valuable and also what makes it expensive: every
semantic decision has to be made twice, and the stabilization programme is
currently making a great many of them. It remains a goal, but a later one.

Until the interpreted language is stable, this ordering holds:

1. Interpreted semantics, the shell, and the REPL.
2. The compiler follows the language rather than constraining it. Compiler work
   is maintenance — keep it building and its guards green — not new surface.
3. A semantic decision is not blocked on the compiler's ability to implement it.
   Where the two disagree, the interpreter is right by definition until this
   decision is revisited.

Revisit when the stabilization programme's P1 tier is closed. Recorded here
rather than only in conversation because it changes what *not* to work on, and
that is the kind of decision that silently reverts.

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

### Completed Phases

| Phase | Focus | Summary |
|-------|-------|---------|
| 1 | Object Model & Display | Typed value layer, display profiles for 40+ CLR types, cell formatters, raw/table/detail/compact display modes |
| 2 | REPL Quality | Tab completion, multiline editing, reverse search, syntax highlighting, paging, ghost text, dropdown picker |
| 3 | Diagnostics & Observability | Pointed errors with notes/spans/help, `inspect`/`members`/`describe-type`/`constructors`, full-screen `help browse` |
| 4 | Language Core | Variables, closures, `if`/loops/`match`/`switch`, `func`/`class`/`enum`/`record`/`module`, `try`/`catch`/`finally`, visibility modifiers |
| 5 | External Command Integration | Native process invocation, redirection, background jobs, typed Linux adapters, `pipefail`, globbing |
| 6 | Modules & Extensibility | `module`/`using`/`require`/`export`, startup chain, `$tosh.Config`, live themes, `view columns` |
| 7 | TUI Platform | `ITuiHost`/`TuiApplication` runtime, reusable widgets, layout system, `help browse`/`config browse`, inline prompts |

## Current Direction: Phase 8 — Daily-Driver Hardening

Phases 1–7 shipped the core shell, language, display system, CLR interop, TUI platform, and extensibility model. Phase 8 is about making TōSh reliable and complete enough to be a default login shell.

The work is tracked in [BACKLOG.md](BACKLOG.md) and falls into these areas:

### Remaining work

- **Shell hardening**: native/object/text boundary polish
- **Unix command parity**: deeper `ip` adapter coverage
- **TUI platform**: extract reusable widgets, form editors and structured input widgets

### Completed in Phase 8

- **Login shell preparation**: PKGBUILD, `/etc/shells`, `SHELL` env var, `PATH`, SIGHUP/SIGTERM handlers
- **Performance under volume**: R2R + uncompressed publish, uid/gid caching, single-pass column widths, ANSI early-exit, display profile caching. Startup 265ms → 55ms, `ls /usr/bin` 265ms → 100ms. NativeAOT ruled out (Reflection.Emit, Activator.CreateInstance, Type.GetType dependencies).
- **Language surface**: `using`/`import` split
- **Unix command parity**: `ps --tree`/`--forest`, `cp` link behavior

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

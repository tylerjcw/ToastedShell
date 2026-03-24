# Tosh Roadmap

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

- File permissions should be a `FilePermissions` value, not a raw `"drwxr-xr-x"` string.
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

- `FilePermissions` instead of `Mode : string`
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

- `FilePermissions` renders as `drwxr-xr-x` in tables, but remains queryable as a typed object.
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

### Phase 1: Object Model and Display Foundations

This is the highest-priority area right now.

Goals:

- strengthen the boundary between runtime objects and textual rendering
- replace stringly-typed shell values with semantic types where it matters
- expand display profiles beyond the current table heuristics

Concrete work:

- introduce `FilePermissions`
- revisit `FileSystemEntry` so its properties represent domain values first and display aliases second
- add richer display profiles for:
  - `FileSystemEntry`
  - `DateTime` / `DateTimeOffset`
  - exceptions
  - dictionaries and common collections
  - inspection/diagnostic objects
- support cell formatters distinct from raw property values
- support raw/table/detail display modes more explicitly
- add user-configurable date/time and size rendering

Success criteria:

- `ls | get Mode | inspect` shows a permissions object, not `System.String`
- `ls` still renders beautifully as a table
- filtering and sorting continue to operate on typed values

### Phase 2: REPL Quality

Goals:

- make the interactive environment feel polished and dependable
- reduce friction when exploring objects and writing pipelines

Concrete work:

- tab completion for commands, members, paths, and types
- multiline editing that understands brackets, quotes, and pipelines
- history search
- word-wise cursor movement
- syntax highlighting
- paging for large output
- color themes for tables and diagnostics
- terminal-width-aware layouts that degrade gracefully

Success criteria:

- the REPL is good enough that we prefer testing the language inside it rather than through one-off CLI invocations

### Phase 3: Diagnostics and Observability

Goals:

- make errors feel native to the shell
- make object behavior easy to understand

Concrete work:

- broaden structured diagnostics across parser and runtime errors
- add notes, secondary spans, and richer help messages
- add command-specific diagnostics for common user mistakes
- expand `inspect`
- add focused commands like `members`, `describe`, or `schema` if needed

Success criteria:

- common mistakes get pointed, actionable errors
- inspecting unfamiliar objects feels easy

### Phase 4: Language Core

Goals:

- move from command-only pipelines into a real language

Concrete work:

- variables and assignment
- expressions and operators
- blocks / closures
- `if`, loops, and pattern/match-style control flow
- functions and aliases
- script files

Success criteria:

- users can write reusable scripts that still feel shell-native

### Phase 5: External Command Integration

Goals:

- make `tosh` useful in the real world outside purely managed commands

Concrete work:

- invoke native processes
- capture stdout/stderr/exit status as meaningful shell values
- add adapters from text streams into structured objects
- support command composition between external tools and typed `.NET` pipelines

Success criteria:

- `tosh` can work as a daily shell, not just an experimental REPL

### Phase 6: Modules, Profiles, and Extensibility

Goals:

- let users extend the shell and tailor it to their workflows

Concrete work:

- modules and imports
- startup profiles
- configuration for display and REPL behavior
- command discovery from loaded assemblies
- type/display profile registration from modules

Success criteria:

- users can install or write their own shell extensions cleanly

## Immediate Next Steps

The next best chunk of work is:

1. Add top-notch globbing and path expansion:
   hidden-file rules, `**`, command-aware expansion, and a clean escape story.
2. Continue growing the Unix-flavored builtin surface with typed object returns:
   process, environment, command-resolution, and system-introspection commands.
3. Extend control flow after `if` / `for` / `while`:
   `break`, `continue`, `until`, and then closures on top of the same block model.
4. Expand the generic pipeline toolkit:
   sorting, skipping, grouping, distinctness, and projection/transformation commands.

That keeps us aligned with the core thesis:

- the pipeline stays strongly typed
- the terminal stays pleasant to read
- the REPL becomes the primary development surface for the language

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

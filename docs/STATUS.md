# ToSh Status

This document is the current project snapshot for ToSh as of April 3, 2026.

Use it to answer three practical questions:

1. What already works well?
2. What still needs hardening?
3. What should we work on next?

## What Already Feels Strong

### Shell and Language Core

- Object-first pipelines over real CLR values
- ToastScript control flow, functions, modules, classes, records, enums, and exceptions
- Strong CLR interop with `new`, `call`, `cast`, `members`, `constructors`, and `describe-type`
- Native interop with `require native`, `bind`, buffers, and by-ref parameter support

### Display and Inspection

- Rich display profiles for shell-native values and a large set of CLR types
- Configurable rendering for dates, times, sizes, permissions, attributes, and per-type columns
- Matrix and deep nested-table rendering that uses terminal width intelligently
- Structured summaries through `summarize` / `summary`

### Interactive Experience

- Multiline REPL editing, history expansion, reverse search, completion, syntax highlighting, and paging
- Modular prompt system with segment layouts and live preview
- Full-screen help browser
- Full-screen config browser/editor

### Files, Streams, and System Commands

- Path-level file I/O commands
- Managed file handles with explicit lifetime and seek/copy support
- Strong Unix-style built-ins for everyday filesystem and process work
- Structured Linux adapters for `ip`, `lsblk`, `findmnt`, `lscpu`, `lsfd`, and `lsipc`

## What Still Needs Hardening

### Daily-Driver Shell Readiness

The largest remaining gap is not one missing subsystem. It is hardening:

- startup and login-shell behavior
- broken-config recovery and startup fallback expectations
- more shell edge-case coverage around native/object/text boundaries
- more real-world parity polish on high-frequency Unix commands

### Performance and Volume

ToSh still needs more long-run stress on:

- large listings
- short-lived invocations
- startup cost
- sustained REPL and TUI responsiveness

### Adapter Strategy

The JSON-backed Linux adapters are a strength, but they still need a clearer long-term rulebook:

- when ToSh should prefer direct .NET or OS APIs
- when it should wrap machine-readable external tools
- how command families like the systemd tools should feel unified instead of piecemeal

## Recommended Next Work

### 1. Daily-Driver Hardening

- tighten startup and login-shell semantics
- improve failure and recovery behavior
- keep pushing common-command parity where it affects real shell use most

### 2. TUI Platform Reuse

- keep extracting reusable widgets and editor state out of app-specific screens
- build future full-screen tools on the same runtime, not one-off implementations

### 3. Unified Adapter Families

- design grouped command families deliberately, starting with systemd-related commands before implementing them

## What ToSh Already Is

Today, ToSh is already credible as:

- an exploratory REPL
- a scripting shell
- a daily side shell for real work

It is not yet fully ready to be the shell everything depends on by default, but it is much closer to that line than it was when the project was mostly a parser/runtime scaffold.

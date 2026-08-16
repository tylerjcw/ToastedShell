---
id: TOAST-0004
title: "Invert the ExternalProcessCommand coupling so Tosh.Language no longer depends on the shell's command library"
status: complete
area: toast
priority: 1
opened: 2026-08-16
closed: 2026-08-16
---

## Problem

Phase A1 of [the separation plan](../../TOAST_SEPARATION_PLAN.md), and the item that
unblocks every other one.

Measured rather than assumed: deleting `using Tosh.Stdlib;` from `ToshEngine.cs` and
compiling produces **two errors, both for `ExternalProcessCommand`** — one where a
resolved name turns out to be a program on `PATH`, one where `&` requires the stage to
be external. `ExternalCommandLookupStatus` already lives in the runtime, so the lookup
is abstracted; only construction and a type test are not.

That is the entire hard coupling between the language and the shell. A codebase whose
language-to-shell dependency is one type at two call sites is not tangled, and saying
so precisely is what makes the rest of the separation a matter of moving files.

## Acceptance

- [x] Both sites go through an interface the shell registers — `IExternalProcessCommand` for the type test, `IExternalCommandFactory` for construction, both owned by `Tosh.Runtime`
- [x] `Tosh.Language.csproj` no longer references `Tosh.Stdlib`, and a test asserts the assembly reference is absent so a single `using` cannot restore it unnoticed
- [x] External commands, `&` backgrounding and `PATH` resolution behave identically; full suite 5,329 passing
- [x] A language-only host with no launcher reports `tosh.runtime.external_commands_unavailable`, naming the way out, rather than throwing a null reference
- [x] Negative control run: reverting the change fails exactly the two tests that encode it

## Notes

**Done 2026-08-16.** The measurement held exactly: one `using` in one file, two errors,
both `ExternalProcessCommand` — a type test in the background-job path needing a
resolved path, and a construction in command resolution. The shell registers the
factory from the same module initializer that installs its command set, so the two
capabilities arrive together, which is the right pairing: a host that has no commands
has no business launching processes either.

The abstraction is deliberately narrow. `IExternalProcessCommand` adds exactly one
member to `IShellCommand` — the resolved path — because that is all the language needs
to build a job specification. Everything else about running a process stayed put.

Done **before** any rename (A5), so the rename is a find-and-replace over a tree that
already compiles in the target shape.

Pairs with the invariant recorded in the separation plan: nothing under
`~/.config/tosh` may affect the language. `Tosh.Language` already holds zero reads of
a config directory, and this item is the natural place to add the guard test that
keeps it that way.

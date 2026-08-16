---
id: TOAST-0004
title: "Invert the ExternalProcessCommand coupling so Tosh.Language no longer depends on the shell's command library"
status: open
area: toast
priority: 1
opened: 2026-08-16
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

- [ ] Both sites go through an interface the shell registers, not a concrete shell type
- [ ] `Tosh.Language.csproj` no longer references `Tosh.Stdlib` — the check is the reference disappearing, not a code reading
- [ ] External commands, `&` backgrounding and `PATH` resolution behave identically; the suite passes unchanged
- [ ] A language-only host with no shell registered gives a clear diagnostic when a script invokes an external program, rather than a null reference

## Notes

Do this **before** any rename (A5), so the rename is a find-and-replace over a tree
that already compiles in the target shape.

Pairs with the invariant recorded in the separation plan: nothing under
`~/.config/tosh` may affect the language. `Tosh.Language` already holds zero reads of
a config directory, and this item is the natural place to add the guard test that
keeps it that way.

---
id: TOAST-0022
title: "Compiled interpolation drops format clauses and cannot reach a class's Display"
status: open
area: toast
priority: 2
opened: 2026-08-17
---

## Problem

Two rendering divergences survive `TOAST-0014` stage 4, both on the compiled side, both
recorded in `DifferentialExecutionTests.KnownDivergences`.

**A format clause is dropped.** `echo $"{42:X} {3.14159:F2}"` gives `2A 3.14` interpreted
and `42 3.14159` compiled. `BoundUnitEmitter` emits an interpolation hole through
`ConvertToString`, which now calls the renderer — but the hole's `Format` and `Alignment`
never reach it.

**A class's `Display` is not reached.** `class T uses Display { func render() … }` renders
`21deg` interpreted and `DiffTemp` compiled. A compiled class is a real emitted CLR type
rather than a `ToshClassInstance`, so it never answers `TryGetOwnRendering`, and the
renderer falls through to its CLR-object path.

## Acceptance

- [ ] A format clause reaches the renderer from compiled code — `$"{42:X}"` is `2A` on both
      backends
- [ ] Alignment (`$"{$n,6}"`) likewise
- [ ] A compiled class implementing `Display` renders through it
- [ ] A compiled class with a `ToString` renders through that, as the interpreter's does
- [ ] Both cases move from `KnownDivergences()` into `Corpus()`, which is the mechanism's
      own signal that they are fixed
- [ ] A negative control: reverting fails the moved cases

## Notes

Found by the differential corpus added in `TOAST-0014` stage 4 — the first time anything
compared what the two backends *render*. Seven of nine new cases diverged; five were fixed
by pointing `ConvertToString` and `ToshValueFormatter` at `ToastRenderer`, and these two
need emitter and object-model work.

The second is the larger one. It is not really about rendering: a compiled class not
answering an interface the interpreted one does is a gap in the emitted object model, and
`Display` is just the first place it showed. Worth checking what else
`IShellInvocableObject` promises that an emitted class does not.

Sequence with compiler work generally. The interpreted contract is settled and pinned; a
backend catching up to it is exactly the shape Phase C describes.

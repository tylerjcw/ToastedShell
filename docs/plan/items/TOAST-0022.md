---
id: TOAST-0022
title: "Compiled interpolation drops format clauses and cannot reach a class's Display"
status: partial
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

- [x] A format clause reaches the renderer from compiled code — `$"{42:X}"` is `2A` on both
      backends
- [x] Alignment (`$"{$n,6}"`) likewise — left, right, and combined with a format clause
- [ ] A compiled class implementing `Display` renders through it
- [ ] A compiled class with a `ToString` renders through that, as the interpreter's does
- [ ] Both cases move from `KnownDivergences()` into `Corpus()`, which is the mechanism's
      own signal that they are fixed
- [ ] A negative control: reverting fails the moved cases

## Progress — 2026-08-23

**The format clause was lost at lowering, not at emission.** The problem statement blamed the
emitter for not passing the hole's `Format` and `Alignment` to `ConvertToString`, but
`BoundInterpolatedExpression` had no such fields to pass: the parser captured them and the
bound IR dropped them on the floor. They are carried through the IR now and populated by the
lowerer.

Both backends call one new entry point, `ToastRenderer.RenderHole(value, format, alignment)`,
rather than each applying the rule itself — the interpreter's own padding helper delegates to
the shared `Align`. Two implementations are how the two backends came to disagree; one is
what stops it recurring, which is the same move `TOAST-0030` made for `new` and `is`.

The plain path still goes through `ConvertToString`. It is the overwhelmingly common case and
the clauses are exactly what it has nothing to say about, so nothing was gained by routing it
through a wider call.

Worth recording: a first corpus row spelled the clauses `{42:X,6}`, where `X,6` is simply the
format string — both backends agreed on the nonsense, so it would have passed while asserting
nothing. Alignment precedes the format clause, as in .NET: `{42,6:X}`.

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

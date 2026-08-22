---
id: TOAST-0068
title: "A refinement's coercer can put the wrong CLR type in a refined slot"
status: complete
area: toast
priority: 2
opened: 2026-08-22
closed: 2026-08-22
---

## Problem

A refinement declares a base type. Its coercer's result is re-checked against the predicate
but is **never converted back to that base type**, so a coercer returning another numeric
type puts that type in the slot:

```tosh
type TimeoutMs = int where (_ > 0 and _ <= 300000) coerce Math.Clamp(_, 0, 300000)

var ok: TimeoutMs = 500        # System.Int32
var coerced: TimeoutMs = 999999 # System.Double  ← declared `int`
```

`Math.Clamp` resolves to an overload returning `double`, the predicate `300000 <= 300000`
holds, and the value is accepted. Both values have type `TimeoutMs`; one is an `int` and the
other is not.

## Why it matters

The refinement is the language's only mechanism for a *narrowed* type, and this is the one
path where the narrowing is applied. A slot annotated `TimeoutMs` is supposed to guarantee
two things — that the value satisfies the predicate, and that it is an `int`. It guarantees
the first and silently drops the second, and only on the coerced path, so the ordinary case
looks correct.

Downstream this surfaces as arithmetic changing type, a CLR call picking a different
overload, or a `dict<TimeoutMs, …>` hashing two equal values differently.

## The rule that is missing

Convert → test → **coerce → convert again** → test → accept or reject. Only the second
conversion is absent:

```
convert to base type
→ test predicate
→ if invalid and a coercer exists, run the coercer
→ convert the coercer's result to the base type      ← missing
→ test the predicate again
→ return the valid value or throw
```

The re-test after coercion *is* present and works: `TimeoutMs = 0` is rejected rather than
accepted at zero.

## Resolution — 2026-08-22

One step, in the one place the algorithm lives: after the coercer runs, its result is
converted to the refinement's base type before the predicate is asked again.

Only for a **named** refinement type, which is the case that has a declared base to convert
to — an inline `where` on a variable has already been converted against its own annotation
before it reaches here, so it is left alone rather than converted twice.

What was already correct and stayed correct: the post-coercion predicate test. A coercer is
not trusted because it returned, so `TimeoutMs = 0` is still refused — its coercer clamps to
a lower bound the predicate rejects.

Two test expectations were wrong before the code was, and both are worth keeping in mind:
`float` is `System.Single` here rather than `Double`, and a coercer returning a string is
refused as `expression_failed` rather than `refinement_failed`. The second is asserted
loosely on purpose — which code a reader would expect is a fair question, and not one this
item settles.

## Acceptance

- [x] A coerced value has the refinement's base CLR type, asserted per refinement rather than
      for one example — an `int` refinement and a `float` one, so the conversion is to the
      *declared* base rather than to `int` in particular
- [x] A coercer returning something unconvertible to the base type is a diagnostic, not a
      silently mistyped slot
- [x] The interpreted and compiled backends agree, in the differential corpus
- [x] `docs/spec/` states the conversion algorithm, including the second conversion
- [x] A negative control — removing the conversion fails two of the seven tests

## Notes

Found while assessing `docs/refinement-types-dotnet-implementation.md`, which predicts this
exact failure in §3 — *"A coercer must never be allowed to smuggle a value of the wrong CLR
type into a refined slot"* — before it was known to be happening. The document was written
about compiling refinements; the defect it describes is in the interpreter today.

Separately, and in the author's own library rather than the language: `TimeoutMs`'s coercer
clamps to a lower bound of `0` while its predicate demands `_ > 0`, so `0` and every negative
value coerce to something the predicate still rejects. `Math.Clamp(_, 1, 300000)` repairs it.
`PosInt` and `NonNegInt` have the related `Math.abs(int.MinValue)` overflow the document
names.

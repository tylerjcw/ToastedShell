---
id: TOAST-0111
title: "`is` and `as` do not see refinement types, so a type test on one is always false"
status: complete
area: toast
priority: 2
opened: 2026-09-04
---

## Problem

Three surfaces disagreed about whether a refinement type exists, and the one that lied is the one
a reader trusts most.

```tosh
type PosInt = int where _ > 0

var p: PosInt = 5      # resolves, and the predicate is enforced
var q: PosInt = -1     # refinement_failed: "this value failed its 'where' predicate"

5 is int               # true
5 is PosInt            # false   ← wrong
5 is-not PosInt        # true    ← wrong, and confidently so
5 as PosInt            # error: Unknown type 'PosInt' in 'as' expression
```

Annotations resolve the name and run the predicate. `as` says the name is unknown — wrong, but it
fails loudly. `is` answers `false` for every value including the ones that satisfy it, and
`is-not` turns that into a `true`. A refinement type is the thing a type test is most obviously
*for*, so this is worse than the unresolvable-name case `TOAST-0105` still tracks.

Found by the author asking whether `is` applies to refinement types. It should, and the answer
demonstrated that it did not.

## Fix

`is` resolves the refinement through the same lookup annotations use, tests the base type, then
evaluates the `where` clauses. It cannot live in the portable operator runtime: deciding a
refinement means evaluating a predicate, which needs the engine's scopes — the same reason
`TOAST-0105` threaded a resolver rather than reading a static.

**A test does not convert.** `var p: PosInt = "5"` may coerce; `"5" is PosInt` is false, for the
same reason `"5" is int` is. A test reports what a value *is*, never what it could become. This is
the one place the two paths deliberately differ, and it is what stops `is` from silently agreeing
with a coercion the author never asked for.

A refinement over a refinement recurses, so every link's predicate must hold.

`as` is a conversion, so it reuses the annotation path outright, `coerce` clause and all — rather
than teaching `CastAs` about refinements. `0 as Repaired` is `1` where the type declares
`coerce (_ == 0 ? 1 : Math.abs(_))`.

## Acceptance

- [x] `5 is PosInt` is true, `-1 is PosInt` is false, `is-not` negates correctly
- [x] A test does not convert: `"5" is PosInt` is false
- [x] A refinement over a refinement checks every link
- [x] Ordinary type tests are unchanged
- [x] `as` converts through the refinement, runs its coercer, and still fails when the predicate
      cannot be satisfied — with a refinement diagnostic rather than "Unknown type"
- [x] An annotation and a test agree on the same name

## Notes

While fixing the `as` diagnostic, source context was threaded through two fallback operator
evaluators that had never carried it, so a failed cast now points at the expression instead of
reporting against a synthetic `<as>` source with no caret. Both had exactly one caller.

This closes the larger half of `TOAST-0105`'s "`is` and type annotations agree on every name
either accepts" box, which was checked in error on 2026-09-02 and has been unchecked. What remains
there is the *unresolvable* name — `$v is Shapes.Typo` still answers `false` rather than saying
the name resolves to nothing.

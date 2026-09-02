---
id: TOAST-0106
title: "Dispatching a non-commutative operator to the right operand produces a reversed result"
status: complete
area: toast
priority: 2
opened: 2026-08-31
---

## Problem

The documented rule for `a OP b` is:

> If the left operand defines an `OP` method, it is invoked with `b` as the argument. Otherwise,
> if the right operand defines an `OP` method, it is invoked with `a` as the argument (note:
> `$this` is still the right operand).

For a commutative operator that is correct. For `-`, `/` and `%` it is not, and the class has no
way to make it correct, because **the method cannot tell which side it was on**:

```tosh
var p = (new ToastLib.Math.Point2D<double>(1.0, 2.0))

echo (10 * $p)   # Point2D(10, 20)     ✓ commutative
echo (10 + $p)   # Point2D(11, 12)     ✓
echo ($p - 10)   # Point2D(-9, -8)     ✓ the supported direction
echo (10 - $p)   # Point2D(-9, -8)     ✗ should be (9, 8)
echo (10 / $p)   # Point2D(0.1, 0.2)   ✗ should be (10, 5)
```

Both spellings call `Point2D.-(10)`. They are indistinguishable inside the method, so no library
can implement subtraction correctly for both.

## Why it is not a library bug

`ToastLib.Math.Point.tosh` documents "symmetric operator dispatch: `2 * $pt` works even though we
only define the point-on-left form" — true, and it silently extends to the operators where it is
unsound. But there is nothing the library can write instead. Any type with a non-commutative
operator has the same hole: matrices, quantities with units, string-like types, date arithmetic.

The result is silent and plausible, which is the worst combination — `10 - $p` returns a point of
the right shape with the wrong sign.

## Options

1. **A reversed hook.** Python's answer (`__rsub__`). Spelling has to fit ToastScript's
   symbol-named methods: an optional second parameter is the least new syntax —
   `func -(other, reversed)` — with one-argument overloads keeping today's behaviour, so nothing
   existing changes.
2. **Refuse instead of guessing.** Stop dispatching `-`, `/`, `%`, `**` and `//` to the right
   operand and raise the ordinary incompatible-operand diagnostic. Strictly less useful, but
   never wrong, and a one-line change to the dispatch order.
3. **Status quo, documented louder.** The spec already states the mechanism; it does not warn
   that it is unsound for non-commutative operators.

Option 2 is the honest floor and could ship immediately; option 1 is what makes the feature
actually work. They compose — 2 now, 1 later, with 1 removing the refusal.

## Acceptance

- [x] `10 - $p` either yields `(9, 8)` or raises; it never yields `(-9, -8)`
- [x] `$p - 10` is unchanged
- [x] Commutative operators on either side are unchanged
- [x] Whichever option is taken, the spec's dispatch section states the rule for non-commutative
      operators explicitly
- [x] Corpus covers `-`, `/`, `%` with the class on the right, and `+`, `*` as controls

## Progress (2026-09-02)

An operator method may take a second `reversed` parameter and is offered it first, falling back
to the one-parameter form — so an existing class is unaffected and a class opts in to being
correct in the reversed position. `tests/Tosh.Tests/ReversedOperatorTests.cs` pins the ordering,
the commutative controls, the untouched single-argument form, and that the flag is `false` for
the left operand. The specification gained an *Operand orientation* subsection.

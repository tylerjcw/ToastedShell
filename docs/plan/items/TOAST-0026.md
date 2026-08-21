---
id: TOAST-0026
title: "A decimal literal is parsed as a double first, so its extra precision is lost before the cast"
status: complete
area: toast
priority: 3
opened: 2026-08-20
closed: 2026-08-20
---

## Problem

`decimal` carries 28--29 significant digits and `double` carries 15--17. A literal
written for a `decimal` goes through `double` on the way, so the digits `decimal` exists
to keep are gone before the conversion happens:

```tosh
var x = 1.0000000000000001 as decimal
var y = 1.0 as decimal
($x == $y)     # true — they are the same decimal
```

Nothing reports it. The value simply arrives rounded.

## Why it matters more than the example suggests

`decimal` is the type reached for when the rounding of binary floating point is
unacceptable — money, and anything audited. A literal is the most common way to write
such a value, and it is the one path that cannot carry it.

## Acceptance

- [x] `1.0000000000000001 as decimal` is distinguishable from `1.0 as decimal`
- [x] A literal with a `decimal` annotation — `var x: decimal = ...` — takes the same path,
      because the literal is already a decimal before the annotation sees it
- [x] Ordinary `double` literals are unchanged, pinned as a control — including
      `0.1 + 0.2`, which still answers `0.30000000000000004`
- [x] A literal too precise for `decimal` is reported rather than silently rounded, or the
      rounding is stated in `docs/spec/` — **stated.** Beyond `decimal`'s range there is
      nothing to widen into, so `1e300` stays a `double`, and `§Overflow` says a literal is
      a `double` unless writing it as one would lose digits
- [x] A negative control — 5 of 14

## Resolution — 2026-08-20

**A literal widens to `decimal` when the `double` would lose its digits**, and stays a
`double` otherwise. No new syntax: a suffix was the conventional answer and the letters are
gone — `1.5m` is one and a half minutes, `1.5d` is one and a half days, and `M` is free only
because suffix matching happens to be case-sensitive, which is a trap rather than an
opening.

The cost is stated rather than hidden: **a literal's type depends on how many digits it
has.** It is tolerable because the two types already interoperate — `decimal + double` is a
`decimal`, and comparison and rendering work across them — so a widened literal is not
stranded.

### The rule took two attempts, and the second is the one to remember

The first compared the literal against `(decimal)theDouble`. That conversion rounds to 15
significant figures, so `2.718281828459045` widened although its `double` holds all sixteen
of its digits — the rule was too eager by two, and a perfectly ordinary constant became a
`decimal`.

The test is now whether the `double` **kept** the value: its round-trip form is read back
as a decimal and compared with the literal. `0.1` is the case that shows why the naive
"is it exact in binary?" test fails — no `double` is exactly a tenth, so that test would
widen nearly every literal.

Both boundaries are pinned, in both directions.

### Fixing this made a second defect reachable, and it is fixed too

The Notes below say the absence of a decimal transitivity defect was "unproven rather than
established", because the probe could not construct two distinguishable decimals. Widening
the literal made it constructible, and the defect was there:

```tosh
var x = 1.0000000000000001    # a decimal now
(x == 1.0)                    # was true  — 1.0 is a double
(1.0 == 1)                    # true
(x == 1)                      # false     — so `==` was intransitive
```

`==` also disagreed with **key equality**, which correctly held `x` and `1.0` to be
different keys. The cause is the one already fixed for integers against floats: deciding a
decimal against a double *by conversion* drops exactly the digit that distinguishes them.
The same rule now covers it — the floating value is taken at its round-trip form and read
back as a decimal, which keeps `0.1 as decimal == 0.1` true where an exact-binary rule
would not.

**And it had to be added to both implementations**, which it was not at first: `==` still
answered the old way until `ToshEngine.AreEqualAsync` delegated it too. That is the third
time in one session that `ToshEngine.Operators.cs`'s own header has been proved right about
its parallel pair. `Both_paths_agree` gained the row that turns it into a failing test
rather than a thing to remember.

## Notes

Found finishing `TOAST-0018`'s equality box, while checking whether `decimal` had the same
transitivity defect as the integer/float pair. **It does not** — and this is why the probe
could not construct one: the two decimals it tried to compare were already the same value
by the time they existed. The absence of that defect is therefore unproven rather than
established, and re-checking it is part of this item.

Not the same defect as the integer/float exactness rule, which is fixed: that one was about
how two values of different types are *compared*, and this one is about a value being lost
before any comparison happens.

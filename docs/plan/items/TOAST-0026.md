---
id: TOAST-0026
title: "A decimal literal is parsed as a double first, so its extra precision is lost before the cast"
status: open
area: toast
priority: 3
opened: 2026-08-20
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

- [ ] `1.0000000000000001 as decimal` is distinguishable from `1.0 as decimal`
- [ ] A literal with a `decimal` annotation — `var x: decimal = ...` — takes the same path
- [ ] Ordinary `double` literals are unchanged, pinned as a control
- [ ] A literal too precise for `decimal` is reported rather than silently rounded, or the
      rounding is stated in `docs/spec/`
- [ ] A negative control

## Notes

Found finishing `TOAST-0018`'s equality box, while checking whether `decimal` had the same
transitivity defect as the integer/float pair. **It does not** — and this is why the probe
could not construct one: the two decimals it tried to compare were already the same value
by the time they existed. The absence of that defect is therefore unproven rather than
established, and re-checking it is part of this item.

Not the same defect as the integer/float exactness rule, which is fixed: that one was about
how two values of different types are *compared*, and this one is about a value being lost
before any comparison happens.

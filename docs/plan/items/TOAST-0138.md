---
id: TOAST-0138
title: "A narrowing numeric annotation warns about a conversion the runtime then performs"
status: open
area: toast
priority: 3
opened: 2026-09-20
---

## Problem

`var x: float = 1.5` emits `tosh.type.mismatch` — "Cannot assign value of type 'Double' to
variable 'x' of type 'Float'", with the help line "no implicit conversion from 'Double' to
'Float'" — and then assigns `1.5` as a `Single` anyway. The checker and the runtime disagree
about whether the conversion exists, and the runtime is the one that is right.

Measured 2026-09-20, every one of these warning and every one succeeding:

| Annotation | Checker | Runtime |
|---|---|---|
| `var x: float = 1.5` | warns | `Single 1.5` |
| `var x: byte = 5` | warns | `Byte 5` |
| `var x: short = 5` | warns | `Int16 5` |
| `var x: Half = 1.5` | warns | `1.5` |
| `var x: Int128 = 5` | warns | `Int128 5` |
| `var x: decimal = 1.5` | clean | `Decimal 1.5` |
| `var x: long = 5` | clean | `Int64 5` |
| `var x: double = 5` | clean | `Double 5` |
| `var x: uint = 5` | clean | `UInt32 5` |

The clean rows are the widening ones. The checker's assignability rule appears to know only
about widening, so every narrowing annotation — which is the ordinary way to ask for a
specific numeric width — is reported as a mistake.

## Why it matters

A warning that fires on correct code is worse than no warning: it trains the reader to ignore
the category. `tosh.type.mismatch` is the diagnostic that would catch a *real* assignment
error, and `var x: byte = 5` is not one — a literal that fits the target is the most ordinary
thing an annotation is for.

It is also load-bearing for annotating at all. Writing `var index: short = 0` in a loop, or
`var factor: float = 0.5` in graphics code, is the point of having the aliases, and each one
currently produces noise the author must learn to ignore.

## What would close this

- [ ] A narrowing conversion the runtime performs does not warn
- [ ] A conversion that genuinely cannot happen still warns, with a test naming one
- [ ] A literal that does not fit its target — `var b: byte = 300` — is reported, and says so
      rather than reporting the wrong thing
- [ ] The rule is stated in `§Built-in Type Aliases` or beside the assignment rules, rather
      than being whatever the checker happens to implement
- [ ] Widening rows above stay clean — they are the regression guard

## Notes

Found while doing [`TOAST-0061`](TOAST-0061.md), whose acceptance includes `var h: Half = 1.5`
working. The alias half of that is done; this is the rest of it, and it is not specific to
`Half` or to the graphics types — `float`, `byte` and `short` have the same problem and predate
them.

The severity is a warning rather than an error, which is why this has gone unnoticed: the
program runs and produces the right value, so nothing fails.

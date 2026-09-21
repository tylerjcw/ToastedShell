---
id: TOAST-0138
title: "A narrowing numeric annotation warns about a conversion the runtime then performs"
status: complete
area: toast
priority: 3
opened: 2026-09-20
closed: 2026-09-20
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

- [x] A narrowing conversion the runtime performs does not warn
- [x] A conversion that genuinely cannot happen still warns, with a test naming one
- [x] A literal that does not fit its target — `var b: byte = 300` — is reported, *by the
      runtime, which knows the value*; the checker no longer reports the wrong thing
- [x] The rule is stated in `§Built-in Type Aliases`, rather than being whatever the checker
      happens to implement
- [x] Widening rows above stay clean — they are the regression guard
- [x] A decimal literal wider than 64 bits can be written — **found while closing this**

## Notes

Found while doing [`TOAST-0061`](TOAST-0061.md), whose acceptance includes `var h: Half = 1.5`
working. The alias half of that is done; this is the rest of it, and it is not specific to
`Half` or to the graphics types — `float`, `byte` and `short` have the same problem and predate
them.

The severity is a warning rather than an error, which is why this has gone unnoticed: the
program runs and produces the right value, so nothing fails.

## Closed — 2026-09-20

The check is **value-aware**, asked of the literal rather than of the types. `NumericRank`
gained `Half`, `Int128`, `UInt128`, `IntPtr`, `UIntPtr` and `BigInteger`, without which the
types [`TOAST-0061`](TOAST-0061.md) blessed were not numeric to this pass at all.

### The first attempt was wrong, and the existing tests said so

It relaxed `IsNumericWidening` to numeric-to-numeric in either direction. That is the obvious
fix and it breaks three things the suite already pinned:

- `Disallows_numeric_narrowing_double_to_int` — `var x: int = 1.5` **should** warn. The
  runtime refuses it, so the checker was right and the relaxation deleted a true positive.
- `Invariant_interface_rejects_widening_type_arg` and
  `Covariant_interface_rejects_narrowing_type_arg` — variance needs `long` to be
  *non*-assignable to `int`, or an invariant slot accepts anything numeric. The variance
  rules are built on the narrowing rule and cannot survive its removal.

The distinguishing fact is the **value**, not the types: `byte = 5` converts and `byte = 300`
does not; `float = 1.5` converts and `int = 1.5` does not. A rule over types alone cannot
separate them, which is what those three tests demonstrate.

So `LiteralFitsNumericTarget` asks the question of the constant, and only where there is one —
anything that is not a literal keeps the widening rule exactly, so variance is untouched.
"Fits" is decided by converting and converting back and comparing, which is the same question
the runtime answers and rejects a fractional value for an integral target without a rule of its
own. `Half` is handled separately because it is numeric and not `IConvertible`, so
`Convert.ChangeType` throws for it rather than answering.

## A second wall, one step further on

Reported while this was being closed:

```
var y: Int128 = 170141183460469231731687303715884105727
✖ tosh.parser.numeric_literal_overflow — This decimal literal is too large for a 64-bit integer.
  help: use a smaller value, or compute it at runtime where a wider numeric type applies.
```

The lexer capped every decimal integer literal at 64 bits, so `Int128`, `UInt128` and the
long-blessed `bigint` were all nameable and none could be written down — the help line was the
language advising a workaround for a value it has a type for. It predates `TOAST-0061`:
`var b: bigint = <huge>` failed the same way.

A decimal, unsuffixed literal that overflows 64 bits now lexes as a `BigInteger`, and
`BigInteger` converts onward to `Int128`, `UInt128`, `Half` and the pointer-sized integers.
`Int128.MaxValue` and `UInt128.MaxValue` can both be written; a literal wider than the
annotation is still refused.

A literal that *states* its width does not widen — `99999999999999999999999999L` and an
oversized `0x…` still overflow. `100L` is an `Int64` and says so, and widening it would
overrule the author rather than serve them.

Nothing that previously parsed changed meaning: the new path is reached only from source that
used to fail in the lexer.

### This reverses a tested decision, and the reversal is recorded

`NumericLiteralWidthTests.A_literal_past_every_integer_type_is_refused` pinned "past every
integer type is a diagnostic", with the reasoning that the behaviour being removed — *becoming
a `double`* — "is silent, and it loses digits".

Both halves of that objection are about `double`. A `BigInteger` loses nothing: it is exact at
any width, and `bigint` was already blessed. What the old rule cost was that three nameable
types could not be written down.

The silence objection survives in weaker form — a typo'd extra digit now yields a `BigInteger`
rather than a diagnostic — and is written into the test beside the change rather than glossed,
so whoever disagrees can find the reasoning and overturn it.

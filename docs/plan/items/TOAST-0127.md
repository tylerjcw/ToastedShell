---
id: TOAST-0127
title: "A numeric comparison meant something different when it could be constant-folded, because the fold compared through decimal"
status: complete
area: toast
priority: 1
opened: 2026-09-11
---

## Problem

**`==` gave two different answers for the same two numbers, depending on where they came from.**

```tosh
0.3 == 0.30000000000000004        # true
$a == $b                          # the same two doubles: false
```

`ConstantFolder.ToDecimalCompare` converted both operands to `decimal` before comparing.
`Convert.ToDecimal(double)` keeps 15 significant digits, so two distinct doubles collapsed onto
one value and compared equal — while the evaluator, which the fold is supposed to be an
optimisation of, compared them exactly and disagreed.

The comment on that method said it matched the runtime's comparison semantics. It did not, and
the arithmetic sitting beside it always had: `ToDecimalIfNeeded` picks the narrowest type that
holds both operands and only reaches `decimal` when one of them already is one. Only the
comparison path forced decimal.

Every folded comparison was affected, so the visible cases were the ones where a fold was
possible at all — both sides literal. That is also why it survived: those are exactly the
comparisons a test is most likely to write, and `0.1 + 0.2 == 0.3` answering `true` looks like
a *feature* until you notice the same expression answers `false` a line later through variables.

Found while writing the geometry tutorial's section on comparing computed numbers, where the
intended lesson was that floating point is not exact and the interpreter appeared to disagree.

## Fix

`NumericEq` and `NumericCmp` now use the same type ladder as the arithmetic beside them —
decimal only when an operand is a decimal, then double, then long, then int.

Ordering against NaN is declined rather than folded. Every comparison against NaN is false at
runtime and no single integer return encodes that, so `NumericCmp` answers null and the fold
does not happen.

## Evidence

Measured before and after on the same file, folded expression against the same values through
variables:

| expression | before (folded) | before (runtime) | after (both) |
|---|---|---|---|
| `0.3 == 0.30000000000000004` | true | false | false |
| `(0.1 + 0.2) == 0.3` | true | false | false |
| `1 == 1.0` | true | true | true |
| `1.5 < 2.5` | true | true | true |
| `((1.0 / 3.0) * 3.0) == 1.0` | true | true | true |
| `1000000000000 == 1000000000001` | false | false | false |

## Worklist

- [x] Reproduce, and establish that the fold and the evaluator disagree rather than both being odd
- [x] Compare in the operand type, matching the arithmetic path
- [x] Decline to fold an ordering against NaN
- [x] Unit test that two distinct doubles do not fold equal
- [x] Theory that integer and mixed comparisons still fold as before
- [x] Theory that every comparison means the same with folding on and off
- [x] Full suite green (7453 passed, 0 failed, 1 skipped)

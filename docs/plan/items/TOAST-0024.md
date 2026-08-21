---
id: TOAST-0024
title: "A range's right operand does not parse the bitwise levels, so `1 .. 2 bor 4` fails"
status: complete
area: toast
priority: 3
opened: 2026-08-19
closed: 2026-08-20
---

## Problem

The precedence chain places the bitwise operators tighter than `..`, and the left
operand honours that. The right operand does not parse at all:

```tosh
(1 bor 2 .. 4)      # 3, 4 — `(1 bor 2) .. 4`, as the chain says
(1 .. 2 bor 4)      # error: A closing ')' is required here
```

The same expression fails differently in statement position, which is worth recording
because neither message names the cause:

```tosh
var r = 1 .. 2 bor 4
# error: Expression pipeline stages must be separated by '|'
#   pointing at `bor`
```

`ParseRangeExpression` descends through `ParseBitwiseOrExpression`, so the left
operand reaches the whole chain. The right operand is read by `ParseRangeArgument`
instead — a separate path that stops short of the bitwise levels, and then reports
whatever it found next as a syntax error belonging to the enclosing construct.

## Acceptance

- [x] `1 .. 2 bor 4` parses as `1 .. (2 bor 4)` and yields `1..6`
- [x] The same for `shl`, `band` and `bxor` in the right operand
- [x] `1 bor 2 .. 4` is unchanged, pinned as a control
- [x] Whatever else `ParseRangeArgument` is missing relative to
      `ParseRangeExpression` is enumerated rather than fixed one operator at a time —
      **done by not enumerating it.** The gap was not a list of operators but one wrong
      entry point: the operand was parsed at the additive level. Pointing it at
      `ParseBitwiseOrExpression` — the level `ParseRangeExpression` already uses for the
      left operand — closes every bitwise level at once and cannot leave one behind
- [x] A negative control — 5 of 12

## Resolution — 2026-08-20

One line. `ParseRangeArgument` parsed an expression-form operand with
`ParseAdditiveExpression`; it now uses `ParseBitwiseOrExpression`, which is what
`ParseRangeExpression` already used for the **left** operand. The two sides of one operator
had been disagreeing about their own precedence.

The argument form is deliberately untouched: in a command's arguments a range operand stays
primary-only, which is what keeps `seq 1..5` from swallowing what follows. Pinned as a
control, along with the stepped form `0 .. 2 .. 8`.

**Not a list of missing operators.** The acceptance asked for the gap to be enumerated
rather than patched one operator at a time, and enumerating it turned out to be the wrong
shape of answer: there was one wrong entry point, not four missing cases. Fixing the entry
point closes every level at once and cannot leave one behind.

## Notes

Found writing the precedence guard for `TOAST-0003`. The guard needed an expression
that distinguishes `bor` from `..`, and the natural one — putting the `bor` on the
right — does not parse; the guard uses the left operand and says why.

Not a precedence defect. The documented order is what the parser implements wherever
the expression parses at all, so `TOAST-0003`'s table is correct as written and this
is a separate gap in one of the two paths that read a range.

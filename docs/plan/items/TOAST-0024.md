---
id: TOAST-0024
title: "A range's right operand does not parse the bitwise levels, so `1 .. 2 bor 4` fails"
status: open
area: toast
priority: 3
opened: 2026-08-19
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

- [ ] `1 .. 2 bor 4` parses as `1 .. (2 bor 4)` and yields `1..6`
- [ ] The same for `shl`, `shr`, `band` and `bxor` in the right operand
- [ ] `1 bor 2 .. 4` is unchanged, pinned as a control
- [ ] Whatever else `ParseRangeArgument` is missing relative to
      `ParseRangeExpression` is enumerated rather than fixed one operator at a time —
      the two paths reading the same grammar differently is the actual defect
- [ ] A negative control

## Notes

Found writing the precedence guard for `TOAST-0003`. The guard needed an expression
that distinguishes `bor` from `..`, and the natural one — putting the `bor` on the
right — does not parse; the guard uses the left operand and says why.

Not a precedence defect. The documented order is what the parser implements wherever
the expression parses at all, so `TOAST-0003`'s table is correct as written and this
is a separate gap in one of the two paths that read a range.

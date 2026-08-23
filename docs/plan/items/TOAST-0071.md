---
id: TOAST-0071
title: "`not` over a rune argument returns a stale answer once a rune has two call sites"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

A rune parameter used as the operand of `not` stops tracking its argument as soon as the
rune is called more than once. Measured 2026-08-22 with `Tosh.Cli --no-profile -c`, after
the sealed-scope fix in `TOAST-0069` (which this survives — it is a separate defect):

| Program | Expected | Actual |
|---|---|---|
| `rune r(c) { writeline (not $c) }` / `r false` | `true` | `true` |
| …with `r false` **and** `r true` | `true` `false` | `false` `false` |
| `rune r(c) { writeline $c }` / `r false` / `r true` | `false` `true` | `false` `true` |
| `rune r(c) { writeline (not false) }` / two calls | `true` `true` | `true` `true` |

One call is right. A second call makes **both** answers wrong, and only when the operand is
a parameter — a literal operand is fine, and the bare parameter without `not` is fine.

It is worse than a wrong value when the result is a condition, because both arms can run:

```tosh
rune r(c, b) { if (not $c) { $b } }
r true  { writeline "A" }   # not true  → false → should not run
r false { writeline "B" }   # not false → true  → should run
```

prints **`A` and `B`**. Reversing the two calls prints nothing at all — the first call's
correct output disappears as well, with exit code 0 and no diagnostic.

## Why this is filed separately from TOAST-0069

`TOAST-0069` is about compiling runes, and its scope fix (a sealed thunk evaluating in the
caller's scope rather than layered over the rune's own parameter scope) cured a stack
overflow in the same area. This one is untouched by that fix and reproduces identically
before and after it, so it is its own defect with its own cause.

The compiled backend is not obviously affected — expansion substitutes the argument syntax
at each use — but the two backends must agree, so this blocks a differential corpus case
for a conditional rune. That is the shape the corpus exists to catch, and it currently has
no rune case with `not` in it for exactly this reason.

## Where to look

The value is neither "the first argument" nor "the latest" — `(false, true)` yields
`false, false`, which is *neither* call's correct answer. That rules out a plain
last-write-wins cache and points at something keyed on the operand syntax node, which is
shared across expansions because the rune body's AST is shared. Start at the unary `not`
path in `OperatorEvaluator` and at whatever memoizes an operand on `ArgumentSyntax`; check
both the engine and `OperatorEvaluator` paths, as the two disagree elsewhere.

## Acceptance

- [ ] `not $param` returns each call site's own answer, for two or more call sites
- [ ] `if (not $param)` runs exactly the arms it should, in either call order
- [ ] The first call's output is never lost when a later call is added
- [ ] A differential corpus case for a conditional rune — the one this currently blocks
- [ ] A negative control: revert the fix and confirm the two-call-site case fails

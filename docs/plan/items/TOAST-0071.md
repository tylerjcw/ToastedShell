---
id: TOAST-0071
title: "Rune expansion stamped a fold onto the shared body AST, so one call site answered for the next"
status: complete
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

## It is a regression from TOAST-0069, not a pre-existing defect

**Filed wrongly on 2026-08-22 and corrected on 2026-08-23.** The original filing said this
was "untouched and uncaused by" the rune-expansion work, and `TOAST-0069`'s notes and commit
message repeat that claim. It was an assumption, not a measurement.

Building the parent commit (`e9d968e`, immediately before rune expansion landed) and running
the three failing programs returns `true false`, `true false`, and `A` — all correct. The
defect arrives with expansion and nothing else, which is exactly what the cause below
predicts: before expansion a rune body's `$c` lowered to a variable reference and never
folded, so nothing was ever stamped.

The lesson is narrow and worth keeping: "this survives my fix" was checked, and "this
predates my change" was not. Those are different claims, and only the second one exonerates.

## Cause — narrowed 2026-08-23

It is **constant folding writing its answer onto a shared syntax node**, not anything
specific to `not`. Four measurements place it:

| Program (two calls, `false` then `true`) | Result |
|---|---|
| `writeline (not $c)` | `false` `false` |
| `writeline ($c == false)` | `false` `false` |
| `writeline (not ($c))` | `true` `false` — **correct** |
| the same body in two *different* runes | `true` `false` — **correct** |

So it is any folded operator over the parameter, it is cured by parenthesising the
operand, and it is per rune *definition*. `OperatorArgumentSyntax.FoldedConstant` and
`UnaryOperatorArgumentSyntax.FoldedConstant` are mutable properties on the AST
(`Parsing/ArgumentSyntax.cs:157` and `:209`), read at
`ToshEngine.Arguments.cs:1522` and `:1607` to skip sub-evaluation. A rune body's AST is
shared by every expansion of that rune, so the fold computed for one call site is returned
verbatim to the next.

`false false` rather than `true true` fits: both calls answer against the *last* argument
bound, which is what a fold resolved once against a mutable parameter binding produces.

**A rune parameter is not a constant.** The fix is to refuse to fold an operation whose
operand resolves to a `RuneThunk` — or, more conservatively, to skip the fold cache while
inside an expansion. Check both the engine and `OperatorEvaluator` paths, as the two
disagree elsewhere.

## Resolution — 2026-08-23

`BuildBinary` and `BuildUnary` still replace the bound node with a literal — that fold is
computed per expansion and is correct — but they no longer write `FoldedConstant` onto the
syntax node while `LowerContext.IsExpandingRune` holds. The bound tree is built fresh for
each expansion; the syntax tree is shared, and only the shared one could carry an answer
across call sites.

Suppressing the stamp *everywhere* would also have passed every assertion below, and would
have quietly cost the interpreter its constant folding, so a control asserts that an
expression outside a rune is still stamped.

## Acceptance

- [x] `not $param` returns each call site's own answer, for two or more call sites
- [x] `if (not $param)` runs exactly the arms it should, in either call order
- [x] The first call's output is never lost when a later call is added
- [x] A differential corpus case for a conditional rune — three, covering a unary operator,
      a comparison, and a condition
- [x] A negative control: reverting the fix fails all seven new assertions, while the
      folding-still-happens control keeps passing

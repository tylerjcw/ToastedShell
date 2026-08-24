---
id: TOAST-0073
title: "A compiled subexpression argument is not held to the one-value rule the interpreter enforces"
status: complete
area: toast
priority: 2
opened: 2026-08-23
---

## Problem

A subexpression used as an argument must produce exactly one value. The interpreter says so
and refuses otherwise:

```tosh
echo ((echo 1 2) | count)
```

```
✖ error  tosh.runtime.subexpression_requires_single_value
  Subexpressions used as arguments must produce exactly one value.
  1 │ echo ((echo 1 2) | count)
          ┄┄┄┄┄┄┄┄┄┄─▶ this subexpression produced 2 values
```

The compiled backend does not check, and evaluates it to `2`.

## How it was found

While closing `TOAST-0067`. That item's fix made `echo 1 2` yield two values on **both**
backends rather than one joined string compiled, and a two-value `echo` inside a
subexpression argument is exactly the shape this rule exists to refuse — so the rule became
reachable on the compiled side for the first time.

Worth recording as a pattern: fixing one backend's arity bug is what exposed the other's
missing arity *check*. A corpus case written for the first found the second.

## Which is right

The interpreter. The rule is a real one — an argument is one value — and a backend that
silently accepts two is not more permissive, it is producing a program the language says is
ill-formed. It also means the two backends disagree about which programs *exist*, which is a
worse class of divergence than disagreeing about a value.

## Acceptance

- [x] A compiled subexpression argument producing more than one value is refused, with the
      same diagnostic code as the interpreter
- [x] The zero-value case is checked too, and behaves the same on both backends
- [x] A subexpression producing exactly one value is unaffected — the control
- [x] The case moves from `KnownDivergences()` into `Corpus()`
- [x] A negative control — restoring the old emitter call fails 1 of 11 focused tests; the
      zero/one controls are among the 10 that stay green

## Resolution — 2026-08-24

The existing compiled multi-stage path was already right: it ended in
`DrainSubexpressionValue`, which collapses zero items to `null`, returns one item, and refuses
more than one. A single-stage built-in command never reached that drain. It used
`InvokeValue`, whose deliberate general-purpose behaviour is to package multiple outputs in
a list, so `(echo 1 2)` became one list-valued argument and the invalid program continued.

`BoundSubexpression` now asks the pipeline emitter for a single subexpression value. That
request is carried through the redirection wrapper and selects a new
`InvokeSubexpressionValue` entry point only for a single-stage built-in command. Ordinary
pipeline-valued assignments still use `InvokeValue`; the change does not turn every pipeline
consumer into a one-value context.

Both runtime-host entry points route through one `CollapseSubexpressionValue` helper, so the
single-stage and multi-stage paths share the zero/one/many rule rather than maintaining two
copies. The differential corpus gained zero-, one-, and multiple-value rows, and the original
multiple-value case moved out of `KnownDivergences()`.

The focused pipeline-value suite passes 11 tests, the differential suite passes 144, the
compiler-emitter and sync/async inventory selection passes 350, and the full suite passes
6,547 with the existing language-surface negative probe skipped. Reverting only the
load-bearing `BoundSubexpression` emitter call fails the new multiple-value assertion and
leaves the other 10 focused tests passing.

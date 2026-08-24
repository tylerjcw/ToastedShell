---
id: TOAST-0073
title: "A compiled subexpression argument is not held to the one-value rule the interpreter enforces"
status: proposed
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

- [ ] A compiled subexpression argument producing more than one value is refused, with the
      same diagnostic code as the interpreter
- [ ] The zero-value case is checked too, and behaves the same on both backends
- [ ] A subexpression producing exactly one value is unaffected — the control
- [ ] The case moves from `KnownDivergences()` into `Corpus()`
- [ ] A negative control

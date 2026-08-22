---
id: TOAST-0065
title: "A compiled `match` over a class hierarchy yields null, so type-pattern narrowing works interpreted and not compiled"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

`match` with **type patterns** over a declared hierarchy compiles cleanly and then produces
`null` at run time:

```tosh
class Shape  { prop K: string = "s" }
class Circle extends Shape { prop K: string = "c" }

var s: Shape = new Circle()
var r: dynamic = match ($s) {
    Circle => "circle"
    Shape  => "shape"
}
echo $r
```

| Backend | Result |
|---|---|
| interpreted | `circle` |
| compiled | throws — *"'r' produced a value that could not be converted to 'dynamic'"* |

The diagnostic names the annotation because that is where the null is *noticed*; even
`dynamic` refuses it, which is what makes the value clearly null rather than merely the wrong
type. Annotating `r: string` reports the same thing about `string`.

## What is and is not affected

Measured on 2026-08-22:

| Shape | Interpreted | Compiled |
|---|---|---|
| `match` on an `int` with literal arms | `two` | `two` |
| `match` on a class hierarchy with type-pattern arms | `circle` | **null** |

So it is narrowing by type that is broken, not `match`.

## Why it was not caught

`TOAST-0036` measured this exact shape and recorded "compiles" — which was true, and was the
question being asked at the time. Nothing ran it. That is the whole finding: a compiled
backend can pass a compilation check and produce a different answer, and only running both
tells you.

It is now in `DifferentialExecutionTests.KnownDivergences()`, asserted to **still** diverge,
so the test fails and says so when it is fixed.

## Acceptance

- [ ] A compiled `match` with type-pattern arms returns the arm's value
- [ ] Narrowing binds the value at its narrowed type inside the arm, as interpreted
- [ ] The case moves from `KnownDivergences()` into `Corpus()`
- [ ] An inherited match — a type pattern naming a base of the runtime type — behaves the
      same on both backends
- [ ] A negative control

## Notes

Found while verifying `TOAST-0036`'s control shapes by running them rather than compiling
them. Related: `TOAST-0022` and `TOAST-0030` record the other interpreted/compiled
divergences by the same mechanism.

---
id: TOAST-0038
title: "The readiness probe is untyped and does not compile, and it is Phase B's exit"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Phase B's exit is *"the probe compiles and runs through the normal IL path without an
interpreter dependency"*. The probe exists — `bench/probes/compiler_shape.tosh`, 371 lines
of lexer, recursive-descent parser, AST hierarchy and two visitor passes — and **it does
not compile**.

It is also not yet a *typed* probe, which the bullet asks for. Its own methods declare no
return types:

```tosh
export class Lexer(source: string) {
    func Tokenize() { … }        # no return type
}
export class Parser(tokens) {    # no parameter type
    func ParseExpr() { … }       # no return type
}
```

So the exit criterion cannot currently be evaluated at all: the probe would fail whether or
not the compiler were ready.

## Measured 2026-08-21

`tosh --compile bench/probes/compiler_shape.tosh` — six errors, two kinds:

| Error | Cause |
|---|---|
| `Compile` is missing a return-type annotation | written that way |
| Parameter `scope` of `Compile` is missing a type annotation | written that way |
| `tokens` could not be pinned down | `(new Lexer($source)).Tokenize()` — a call |
| `ast` could not be pinned down | `(new Parser($tokens)).ParseExpr()` — a call |
| `globals` could not be pinned down | `new System.Collections.Hashtable()` |
| `r` could not be pinned down | `Compile($src, $globals)` — a call |

`--compile-allow-dynamic` removes the four `implicit_dynamic` errors and **not** the two
annotation errors, which are unconditional. So the probe does not compile by either route.

Four of the six are calls, which is `TOAST-0034`. Two are the probe being untyped, which is
this item.

## What this item is, and is not

**It is**: typing the probe end to end — annotating every method, parameter and return —
and treating whatever fights back as the finding. That is the exercise the bullet describes,
and it is how `TOAST-0034` and `TOAST-0036` were found in the first place, from six error
messages.

**It is not**: making the probe compile by weakening it. Annotating a return as `dynamic`
would satisfy the compiler and defeat the point, since the probe exists to find out which
parts of ToastScript fight back when you write compiler-shaped code.

## Acceptance

- [ ] Every function, method, parameter and return in the probe is annotated concretely —
      no `dynamic`, and the reason recorded wherever one is unavoidable
- [ ] `tosh --compile` accepts it with no flags
- [ ] The compiled probe produces the same output as the interpreted probe, asserted rather
      than eyeballed
- [ ] It runs without an interpreter dependency — the Phase B exit sentence, checked
      explicitly rather than assumed from a successful compile
- [ ] Whatever fights back is filed, not worked around
- [ ] A negative control

## Notes

Depends on `TOAST-0034` for four of the six errors. The other two can be fixed immediately
and will change what the remaining errors are — which is the point of doing this alongside
rather than after.

Blocks Phase C, which asks that the interpreter and IL pass the differential corpus; that
corpus is `DifferentialExecutionTests`, now down to three recorded divergences after
`TOAST-0030`.

---
id: TOAST-0076
title: "A module-qualified type annotation is not resolved, and the diagnostic says the annotation is missing"
status: proposed
area: toast
priority: 2
opened: 2026-08-24
---

## Problem

A variable annotated with a module-qualified type does not compile, and the reason given is
that it has no annotation:

```tosh
export module M {
    export class Box { prop X: int = 3 }
}
var v: M.Box = new M.Box()
```

```
✖ error  tosh.compile.implicit_dynamic
  Variable 'v' has no type annotation and the inferrer could not pin down a concrete type.
  4 │ var v: M.Box = new M.Box()
  help: annotate the variable (e.g. `var v: int = ...`) …
```

`v` **is** annotated. The advice is to do the thing that was already done, so a reader
following it has nowhere to go.

## Not specific to any one kind — measured 2026-08-24

| Declaration | Annotated `var` compiles? |
|---|---|
| `class Box` at top level | yes |
| `struct Vec` at top level | yes |
| `M.Box` — module-qualified class | **no** |
| `M.Vec` — module-qualified struct | **no** |
| `M.P` — module-qualified record | **no** |

So it is the *qualification* the inferrer does not resolve, not the kind. Found while closing
the struct rows of `TOAST-0035`, whose table records this as two separate kind-specific
failures — "the property read comes back as something `int` will not take" and the trait and
union rows beneath it. At least the struct row is this defect wearing a kind's clothes, and
the others are worth re-measuring against it before being treated as distinct.

## Two defects, and the message is the worse one

A missing capability that reports itself honestly costs a reader one decision: annotate, or
pass `--compile-allow-dynamic`. This one reports a *false* cause, so the obvious response —
"but I did annotate it" — leads nowhere, and the real answer, that qualified annotations are
unresolved, appears nowhere in the output.

The message is emitted where implicit-dynamic is detected; the check evidently reads
"annotation resolved to a concrete type" and reports it as "annotation present".

## Acceptance

- [ ] A module-qualified annotation resolves, for class, struct and record alike
- [ ] `var v: M.Box = new M.Box()` compiles under `--profile runtime` and agrees with the
      interpreter
- [ ] When an annotation genuinely cannot be resolved, the diagnostic says *that*, and does
      not claim the annotation is absent when it is present
- [ ] The `TOAST-0035` kind table is re-measured against this cause, and rows it explains are
      folded into it rather than left as separate kind-specific failures
- [ ] Differential corpus cases for the qualified forms
- [ ] A negative control

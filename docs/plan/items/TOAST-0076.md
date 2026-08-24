---
id: TOAST-0076
title: "A module-qualified type annotation is not resolved, and the diagnostic says the annotation is missing"
status: complete
area: toast
priority: 2
opened: 2026-08-24
closed: 2026-08-24
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

- [x] A module-qualified annotation resolves, for class, struct and record alike
- [x] `var v: M.Box = new M.Box()` compiles under `--profile runtime` and agrees with the
      interpreter
- [x] When an annotation genuinely cannot be resolved, the diagnostic says *that*, and does
      not claim the annotation is absent when it is present
- [x] The `TOAST-0035` kind table is re-measured against this cause, and rows it explains are
      folded into it rather than left as separate kind-specific failures
- [x] Differential corpus cases for the qualified forms
- [x] A negative control

## Resolution — 2026-08-24

`Lowerer.BuildUserTypeRegistry` now carries the enclosing module path while harvesting
source-declared types. A declaration inside nested modules is available under its qualified
name (`Outer.Inner.Box`) as well as through the existing bare entry used while lowering a
module's own body. The common resolver therefore handles class, struct, record, interface,
enum, trait, union and type-alias annotations without kind-specific cases.

An unresolved written annotation is now recorded independently of the local's inferred
implementation type. That distinction matters for `var value: Missing.Type = 1`: lowering
can retain `int` as useful best-effort information without allowing it to erase the invalid
source contract. The compile diagnostic names `Missing.Type`, says it could not be resolved,
and no longer advises the reader to add the annotation already present.

The runtime-profile corpus now executes a typed `M.Box` and typed `M.Point`; the differential
corpus covers module-qualified class, struct and record values. The negative control uses an
unknown qualified path with a concrete initializer, so reverting only the diagnostic text or
only the registry walk fails independently.

Re-measuring `TOAST-0035` removed the old struct typed-local failure from the struct account.
Trait and union annotations resolve too, but their residual failures remain independent:
trait members are not injected into a using class, and module-qualified union variants need
factories on the union base. Their source-replay tripwires therefore remain.

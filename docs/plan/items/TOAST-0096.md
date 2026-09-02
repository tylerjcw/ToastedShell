---
id: TOAST-0096
title: "A generic union's unit variant cannot infer its type arguments from where the value is going"
status: complete
area: toast
priority: 2
opened: 2026-08-29
---

## Problem

Type-argument inference for a union variant read only the *arguments*, so a variant with no
fields had nothing to infer from and demanded an explicit list — even where the target said
exactly what it was:

```tosh
union Opt<T> { Some(T) None() }

var o: Opt<int> = Opt.None()          # refused: 'T' cannot be inferred
func find(k) -> Opt<int> {
    return Opt.None()                 # refused, for the same reason
}
```

The refusal is well-worded (*"Generic union 'Opt' requires explicit type arguments because 'T'
cannot be inferred. Call Opt.None<T>(...)"*), and `Opt.None<int>()` works. It is the ergonomics
that are wrong: the annotation and the signature had already said `int`, and the author must
repeat it at the one place the compiler could most easily have read it.

## Why it was taken before `TOAST-0083`

`TOAST-0083` provides `Option<T>` and `Result<T, E>` as core types, and `None` is the most
common value in the whole optionality story. Shipping the core types first would have baked
`Option::None<int>()` into every example of the feature, which is exactly how a language teaches
its own friction as idiom. Decided with the user on 2026-08-29; see `DECISIONS.md`.

## Fix

**The plumbing already existed.** `_targetTypeAnnotation` was pushed around an annotated
initialiser with the comment "so generic calls in the initializer can seed bindings from it" —
it simply flowed to `CommandInvocation` and nowhere near union construction.

- `ToshUnionDefinition.ResolveTypeArgumentBindings` consults the target annotation before
  refusing, filling only the parameters the arguments did not name. An argument-inferred binding
  wins: the target says what the slot expects and the value says what is going into it, and
  disagreement there is the annotation's conversion to report rather than this method's to paper
  over.
- Return position needed a second source. The declared return type is not in scope at a `return`
  — the block executors take a block, not a signature — so `_currentReturnAnnotation` is set for
  the duration of the function body and read by the return statement as its target. Assigned
  rather than scoped with `using`, because the body is consumed by an enumeration loop rather
  than by the call that constructed it.

Both cases now infer, and nothing else changed: with no target the original refusal stands, an
explicit `<int>` still wins, and inference from arguments is untouched.

## Verification

`tests/Tosh.Tests/UnionTargetInferenceTests.cs`. Controls cover the unannotated declaration
inside an annotated function's body — which must *not* pick up the signature's type — and a
target naming a different union, which must not seed anything.

## Left open

Inference reads a target from an annotated variable and a declared return type. It does not read
one from a parameter's declared type at a call site (`take (Opt.None())` where `take` declares
`Opt<int>`), which is the same feature one level further out and was not needed by
`TOAST-0083`.

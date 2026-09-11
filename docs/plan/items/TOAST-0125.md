---
id: TOAST-0125
title: "Generics audit: a null type-argument binding disables the checks it feeds, and only one constraint kind is enforced"
status: partial
area: toast
priority: 1
opened: 2026-09-10
---

## Summary

Full findings in [`TOAST-0125_generics_audit.md`](../TOAST-0125_generics_audit.md), measured
by probing rather than reading. Tōast is a .NET language written in one, so C# is the
reference; the question at each divergence is whether it was chosen.

Generics work for the case they were built for — a class or method over one or two plain CLR
types — and fall away from it in three directions:

1. A type argument that is not a plain CLR type binds `null`, and a null binding is read as
   "accept anything". That one mechanism is behind most of the soundness findings, and
   behind `TOAST-0116` and `TOAST-0118` before them.
2. Only `where T: Numeric` is enforced. An interface or base-class constraint is parsed,
   recorded, and never consulted.
3. A closed generic is checked as closed only at construction. Annotations see the open
   type, the contract check never substitutes, and `$x is Box<int>` does not parse.

Inference, inheritance, recursion, multi-parameter generics and CLR generic interop are all
sound and should not be disturbed.

## Worklist

- [x] **D1** `any` resolves to a JS-interop type and fails in every annotation position
- [x] **C** widening at a type-parameter binding — `TOAST-0124`
- [x] **A1–A3** a type argument naming a ToastScript type or alias binds null
- [x] **B2/B3** interface and base-class constraints are never enforced
- [x] **F2** the contract check compares against the open parameter, so every closed generic
      interface implementation reports a false mismatch
- [x] **A4** a generic annotation accepts any closure
- [x] **F1** `Two<int, string>(1, "x")` reports a false arity error
- [x] **F3** `$x is Box<int>` does not parse
- [x] **F5** *withdrawn* — `prop I: int` is null too, so this is uniform across the language
      rather than a generics gap; fixing it for type parameters alone would make generics
      inconsistent with everything else
- [ ] **E1/E2** `trait<T>` and `struct<T>` are not recognised — *features, not defects*;
      see the audit's "Left, as features". A trait injects default bodies, so its parameters
      must substitute through them at application time; a struct has no type-argument
      concept at all and needs the ground this audit covered for classes
- [x] **E3** *withdrawn* — variance **is** implemented, in the type checker, with tests for
      both directions. The audit probe looked at a runtime `fulfills`, which is not where
      variance lives
- [ ] **F4** statics are shared across every closure; C# gives each closed type its own —
      *a decision*, not a defect: a real semantic change with a small payoff

---
id: TOAST-0018
title: "Portable core semantics: the eight Phase A concerns outside formatting and streaming"
status: proposed
area: toast
priority: 2
opened: 2026-08-17
supersedes: TS-P3-16
---

## Problem

`SELF_HOSTING_RFC.md` Phase A names ten concerns to specify:

> equality, hashing, ordering, nullability, overflow, Unicode, formatting, collection
> shape, streaming, and exception semantics

**Phase A is scoped to formatting and streaming** — `TOAST-0014`, `TOAST-0017` and
`TOAST-0015`. This item carries the other eight, so that scope decision does not quietly
lose them.

`TS-P3-16` was the RFC's placeholder for this and is one paragraph. It is superseded here
rather than expanded, because new work does not go into the `TS-P*` system.

## What exists today

Measured in the Phase A survey (`docs/plan/PHASE_A_SURVEY.md`):

| Concern | Where it lives | State |
|---|---|---|
| equality | `OperatorEvaluator.AreEqual` (`:321`) | implemented, unspecified |
| ordering | `OperatorEvaluator.EvaluateOrderedComparison` (`:474`), `TryCompareByName` (`:562`), and a second path at `ToshEngine.cs:2609 CompareCore` | **two implementations** |
| hashing | no central site | **absent** |
| nullability | scattered; `ToshTruthiness.cs` (85 lines) covers truthiness only | partial |
| overflow | no `checked` policy in `OperatorEvaluator` | **unspecified** |
| Unicode | inherited from `System.String` wholesale | **unspecified** |
| collection shape | `TS-P3-04`, status *research*, one-line acceptance | filed, not designed |
| exception semantics | `ToshError.cs` (73 lines) over .NET exceptions | partial |

`TypeConversion.cs` (748 lines) and `OperatorEvaluator.cs` (1,679) are where most of this
actually lives.

## Acceptance

- [ ] **Equality** specified in Tōast terms: which values are equal, across numeric widths,
      `null`, records, collections and class instances — not "whatever `AreEqual` does"
- [ ] **Ordering** specified, and the **two implementations reconciled** — `OperatorEvaluator`
      and `ToshEngine.CompareCore` must not be able to disagree
- [ ] **Hashing** given a contract consistent with equality, since there is no central site
      today and a value model without one cannot back a portable dictionary
- [ ] **Nullability** specified beyond truthiness: what `null` means in comparison,
      arithmetic, member access and collection membership
- [ ] **Overflow** given a policy — wrap, saturate, or raise — stated per numeric type
      rather than inherited from whichever CLR operator was reached
- [ ] **Unicode** specified: what a `str` is made of, what `Length` counts, and how
      indexing, slicing and comparison behave
- [ ] **Collection shape** resolved with `TS-P3-04`, including the recorded asymmetry that
      `[1,2,3] | count` is 3 while a piped dictionary counts as 1
- [ ] **Exception semantics** specified: what is catchable, what a thrown non-error value
      means, and how a `no_clr` target represents one
- [ ] Each of the eight lands in `docs/spec/` as prose *before* implementation, and in the
      backend-neutral corpus after
- [ ] The corpus extends `DifferentialExecutionTests`' pattern — one program, interpreted
      and compiled, asserted equal — rather than starting a second harness

## Notes

**Not Phase A, and deliberately not started.** The scoping decision is recorded in
`DECISIONS.md` for 2026-08-17: formatting and streaming first, because they are what block
`TOAST-0006` and what the survey found to be cheapest and best understood.

This item is the reason that decision is safe. Eight concerns with no owner is how a phase
exits "complete" while its stated goal is unmet.

Sequence after Phase A. Several of these are more expensive than they look — reconciling
two ordering implementations is a behavioural change, and an overflow policy changes
arithmetic results — so each wants the same treatment `TOAST-0014` is getting: specify,
then move, never both inside one diff.

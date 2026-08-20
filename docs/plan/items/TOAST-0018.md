---
id: TOAST-0018
title: "Portable core semantics: the eight Phase A concerns outside formatting and streaming"
status: partial
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
- [x] **Ordering** specified, and the implementations reconciled — **done 2026-08-19**,
      and there were **three**, not two: `OperatorEvaluator` for `<`, `SortCommand`'s
      `ShellSortComparer`, and a simplified copy of the latter in `ToshEngine` for the
      fused `sort | first` path. One comparer now lives in `Tosh.Runtime`; see below
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

## Ordering — 2026-08-19

Specification first (`§Ordering`), corpus from the prose (`ValueOrderingTests`, 34 tests),
then the change. Three findings, each measured:

**`<` was not portable.** String comparison fell through to `string.CompareTo`, which is
culture-sensitive: `("z" < "ä")` answered `false` under `en_US` and `true` under `sv_SE` —
the same program meaning different things on different machines, which is precisely what
Phase A exists to eliminate. .NET carries ICU's collation data, so the locale did not even
need to be installed for the answer to change. Strings now compare by **code point**.

**The decisive argument was not portability but consistency.** Equality compares two
strings exactly, so `"a" == "A"` is false — while a case-insensitive order calls them
*equal*. That is a broken trichotomy: two values neither less, nor greater, nor equal.
Ordering must agree with equality about which values are the same, and only code point
does. `Trichotomy_holds_against_equality` pins it as a property rather than as examples.

**The third implementation was disagreeing in production.** `[1, "a", 2.5] | sort`
answered `1, 2.5, "a"` while `| sort | first 3` answered `2.5, 1, "a"`: the fused copy
compared only values of an identical type and otherwise ordered by type *name*, putting
`Double` before `Int32`. Sharing one comparer makes that class of divergence unwritable
rather than merely fixed — which is what the box asked for.

### The operators and the sort differ by policy, and only there

An operator may refuse a pair with no meaningful order and say so; a sort may not, because
every element has to land somewhere. So `<` raises on booleans, on a string against a
number, and across two enums, while the comparer orders everything via a type-name
fallback. `null` is the same split: outside the order for operators — every direction
`false`, `null < null` included — and sorted first by the comparer. Both halves are
specified.

### `sort`'s default turned over

`sort` now orders by code point, with `-i`/`--ignore-case` to ask for the old behaviour.
`-o`/`--ordinal` is still accepted and now names the default, so a script that asked for
code-point order keeps getting exactly what it asked for.

This is a **visible daily change** for an interactive shell: `ls | sort Name` groups
capitalised names first (`AGENTS.md`, `Directory.Build.props`, … , `artifacts`) where it
used to interleave them. That was weighed and accepted — `TS-P2-75` had already recorded
the opposite complaint, that case folding put `expected_record_fields` before
`expected_record_field_default`, and generated output wants code point.

`sort -u` follows the same rule, so uniqueness and ordering cannot disagree about case.

**Negative control: 14 of 44 fail** with the three source changes reverted and the tests
kept. Suite 5,904 passing.

### Still open here

Seven concerns remain. **Equality** is partly covered already — `TOAST-0003` rewrote the
cascade in the specification as five ordered steps, verified against the binary — but its
box asks for numeric widths and class instances too, which that rewrite did not cover.

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

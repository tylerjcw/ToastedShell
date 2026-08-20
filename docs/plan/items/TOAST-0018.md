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

- [x] **Equality** specified in Tōast terms: which values are equal, across numeric widths,
      `null`, records, collections and class instances — not "whatever `AreEqual` does" —
      **done 2026-08-20**, see below
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

## Equality — 2026-08-20

`TOAST-0003` had already rewritten the cascade; this added what the cascade named nowhere —
numeric widths, `null`, class instances and the float specials — as
`§Numbers, null and Instances`, with `ValueEqualityTests` written from it.

**Equality was not transitive.** An integer compared against a floating value was decided
by conversion, and a 64-bit integer above 2^53 has no exact `double`:

```tosh
var a = 9007199254740993 as long
var c = 9007199254740992 as long
var b = ($c as double)
($a == $b)   # was true
($c == $b)   # true
($a == $c)   # false
```

A relation where `x == y` and `y == z` do not give `x == z` cannot back a dictionary, a
`distinct`, or a cache — so this is a defect in the value model rather than a rounding
wart. An integer now equals a floating value only when that value is finite, integral and
the same number. Converting the *float to the integer* is what makes it exact: an integral
double inside `long`'s range converts with nothing lost.

**The fix landed on the wrong implementation first, exactly as the file predicted.**
`OperatorEvaluator.AreEqual` and `ToshEngine.AreEqualAsync` are structurally parallel, and
`ToshEngine.Operators.cs` opens by recording that `TS-P1-14`'s fix "landed only on the
synchronous side — `==` goes through here, so the defect survived a change that looked
complete". Adding the rule to the evaluator changed nothing observable, the suite stayed
green, and only measuring `==` against the binary showed it. The engine now **delegates**
to the shared rule, the way it already delegates `TryCompareByName`.

`Both_paths_agree` is the guard that makes this fail rather than pass silently, and it
needed a case above 2^53 to have any force — with only ordinary values it passes while one
implementation carries the rule and the other does not. **Negative control, engine side
only reverted: it fails on exactly that pair.** Both sides reverted: 3 of 39 fail.

### Decisions recorded rather than inherited

**`NaN` equals itself**, which is deliberately not IEEE 754's `==`. Equality is the relation
collections are built on and has to be reflexive; under the IEEE rule a `NaN` in a
dictionary could never be found again. Signed zeroes follow IEEE and compare equal.

**A class instance is equal only to itself** unless the class declares `equals`, which is
the opposite default from a `record` — a record is a bag of values, a class has identity.

Suite 5,943 passing.

### Still open here

Six concerns remain: hashing, nullability, overflow, Unicode, collection shape and
exception semantics — plus the backend-neutral corpus that is Phase A's exit.

**Hashing is next by dependency.** Its box asks for a contract consistent with equality,
and equality is now specified, so the question is answerable for the first time. It is also
the concern with no implementation at all today.

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

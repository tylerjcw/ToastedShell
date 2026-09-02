---
id: TOAST-0095
title: "`is` answers false for a nested type, and a qualified variant pattern never matches"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

Two separate places where a *qualified* type name is accepted and then quietly fails to mean
anything. Both were found while probing `TOAST-0090`, and neither is caused by it — both
reproduce identically in the dotted spelling that predates the path operator.

### `is` against a nested type is always false

```tosh
class Outer { class Inner { } }
var i = new Outer.Inner()
$i is Outer.Inner        # false
```

The value *is* an `Outer.Inner` — it was just constructed by that name, and the same name
works as a type annotation (`var i: Outer.Inner = new Outer.Inner()` binds). Only `is`
disagrees. It answers `false` rather than raising, so a narrowing `if` silently takes the wrong
branch instead of reporting an unknown type.

### A qualified variant pattern never matches

```tosh
union Result { Ok(v), Err(m) }
var r = Result.Ok(42)

echo (match ($r) {
    Ok(v) => $v                 # 42 — the bare variant name matches
    default => 0
})

echo (match ($r) {
    Result.Ok(v) => $v          # 0 — the qualified name falls through to default
    default => 0
})
```

The bare form works; qualifying it with the union that declares it stops the arm matching. No
diagnostic is raised, so the arm looks live and the value silently takes `default`. This is the
worse of the two: an unreachable arm that reads as reachable, in the construct whose whole
purpose is exhaustive dispatch.

`TOAST-0054` added exhaustiveness checking over variant patterns, which makes this sharper — a
match written entirely in qualified arms would be judged on arms that can never fire.

### A nested type constructs differently when compiled

```tosh
class Outer { class Inner { prop V = 7 } }
echo ((new Outer.Inner()).V)     # 7 interpreted; diverges compiled
```

Recorded in `DifferentialExecutionTests.KnownDivergences` under this item rather than fixed:
the interpreter is authoritative and compiled ToastScript stays an experiment. Noted here
because it is the same nested-type name resolution as the `is` defect above, and the two may
share a cause.

## Why they belong together

Both are the same shape: a qualified name is parsed, accepted, and then compared against
something that only ever holds the bare one. Worth confirming whether the variant pattern and
`is` reach the same comparison, in which case it is one fix.

The `::` spelling behaves identically to `.` in both cases — `Result::Ok(v)` also falls through
— which is expected, since paths canonicalise to dots before resolution. Whatever fixes the
dotted form fixes both spellings at once, and `TOAST-0090`'s corpus should gain the qualified
pattern case once it matches.

## Acceptance

- [ ] `$value is Outer.Inner` answers true for an instance of the nested type
- [ ] `Outer::Inner` behaves identically in `is`
- [x] A qualified variant pattern matches exactly when its bare form does
- [x] A pattern naming a variant of the wrong union is a diagnostic, not a silent non-match
- [x] Exhaustiveness checking counts qualified arms as covering their variant
- [ ] Interpreter and compiler agree

## Qualified variant patterns — fixed 2026-08-29

Taken ahead of `TOAST-0083`, which it blocks: `Option` and `Result` are meant to be *core*
types, so `Result.Ok(v)` is the spelling their users reach for, and every such arm was silently
dead.

Two halves, and the second was the dangerous one:

- **The matcher** compared the pattern's whole name (`QvpMaybe.Some`) against the variant name
  (`Some`), so it never matched. `PatternSubject` now carries the declaring union as `OwnerName`,
  and a qualified pattern is checked against it rather than suffix-matched — so a qualifier
  naming *another* union is `tosh.runtime.pattern_wrong_qualifier` rather than a silent miss.
  That case matters because two unions may both declare `Some`.
- **The binder's exhaustiveness check** keyed on the same bare name, so the lookup missed and it
  `return`ed — it did not miscount qualified arms, it went silent entirely. A match written in
  qualified arms was neither judged exhaustive nor reported incomplete.

`::` is canonicalised into the pattern name at parse time, so `QvpMaybe::Some(v)` and
`QvpMaybe.Some(v)` are one form by the time anything compares them, and arms may mix the three
spellings freely.

`tests/Tosh.Tests/QualifiedVariantPatternTests.cs` — 10 tests; the negative control fails 6.
Note that the exhaustiveness test must push `BinderStrictness.Strict`: under the default `Warn`
a bind-time report goes to the error stream rather than throwing, so the test would pass whether
or not the check existed.

## Still open

The `is` defect and the compiled nested-type divergence above are untouched.

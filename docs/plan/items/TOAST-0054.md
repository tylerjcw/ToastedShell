---
id: TOAST-0054
title: "A `match` over a closed union is not checked for exhaustiveness"
status: partial
area: toast
priority: 1
opened: 2026-08-22
---

## Problem

`match` has `default`, and nothing requires the arms to cover the variants of a closed
union. Adding a variant to a union compiles every existing `match` over it unchanged, and
the omission surfaces at runtime on whichever input first reaches the new case.

`docs/SELF_HOSTING_RFC.md` states the intent — a `union` is "a closed tagged sum suitable
for exhaustive matching", and closed compiler trees "prefer unions and records because
matches can be exhaustive". Suitability is not checking. Nothing today makes the match
exhaustive; it merely could be.

## Why this is the difference between a maintainable compiler and an unmaintainable one

The value is not in catching the first mistake. It is in what happens when a node type is
added to a syntax tree: either the compiler reports the eleven `match` sites that must be
updated, or the author finds them over the following weeks, in production, on someone
else's input.

Every ML-family compiler is built on this property. It is the single largest correctness
benefit the union/match pair provides, and it is the one the language currently does not
collect.

## Why it must arrive with the other two, not after

Exhaustiveness is a front-end check — it belongs in `TypeChecker`, which is already shared
by both backends, so it is single-site work. It could technically ship before
`TOAST-0052` and `TOAST-0053`. It should not: over an untyped, string-tagged union there
is nothing to be exhaustive *about*.

It also must be an **error** from the first release that has it. Retrofitting exhaustiveness
onto a codebase that has accumulated non-exhaustive matches is a large mechanical migration,
and the pressure to make it a warning is exactly how other languages ended up with a check
nobody enforces. An explicit `else` arm is the opt-out for cases that genuinely want one.


## First slice — 2026-08-28

The check runs, as an error, on every `match` whose arms are variant patterns of a union
declared in the same source. It names the uncovered variants rather than reporting mere
incompleteness, `default` is the opt-out, and a guarded arm does not cover its variant.

### The union comes from the arms, not from the value's type

This item says the check "belongs in `TypeChecker`, which is already shared by both backends".
That is true of where it will eventually live and false about what is there now:
`TypeChecker.cs` is 2,489 lines with no reference to `MatchArgumentSyntax`,
`UnionDefinition` or `ToshUnion`. It knows nothing about match or unions, so routing through
it would have meant teaching it both first.

It did not need to. **A variant name belongs to exactly one union**, so a single arm names the
set — `Lit(v)` identifies `Expr`, and `Add` and `Neg` are then reportable without knowing
anything about the matched value. `TOAST-0053`'s binder already collects same-source union
declarations, so the check is a coverage count over a map that existed.

The cost of taking the arms rather than the type is one case: a `match` with **no** variant arm
at all is not recognised as a union match. Since such a match binds nothing out of the union
and would need `default` anyway, that costs nothing real.

### What exempts a match, and why each

A `default` arm — the documented opt-out. An arm that is not a variant pattern, meaning this is
not a union-shaped match, which is what keeps ordinary shell code free of the check. And a
union declared in another source, which is invisible here and about which nothing is claimed.

A guarded arm does not cover its variant: it may not fire, so it cannot complete the match.
This is the one refusal that reads like a false positive until explained, so the help explains
it rather than leaving the author to work it out.

### Blast radius: nothing

Every `.tosh` file in `examples/`, `scripts/` and `tests/` binds clean — 32 files, zero
diagnostics — as do the 24 files of the author's own `~/.config/tosh`. The suite needed no
changes. Making it an error from the first release, which this item insists on, cost nothing
because there was nothing to retrofit.

### One regression found and fixed

Binding the real files surfaced `$frames` in `examples/mandelbrot.tosh` reported as undeclared.
It is declared — by `arg frames : PosInt = 100` — and the binder had never declared script
input parameters. That cost nothing while the binder walked only command arguments, because the
reference sits in `var tF = $frames`, an expression stage the walk skipped; `TOAST-0053`
widened that walk and made a real defect visible. `ScriptInputStatementSyntax` now declares its
parameters, and a test pins it.

### Nested coverage is not done, and a conservative version would be worse than none

Coverage is counted at the top level. An arm that destructures deeper — `Add(Lit(a), r)` —
still counts as covering `Add`, so a match can be accepted that a nested value falls through.
That is a *missing* report, never a wrong one.

The tempting shortcut is to say a variant pattern with a refutable sub-pattern does not cover
its variant. That is unsound in the other direction: a `match` with arms for `Add(Lit(a), r)`,
`Add(Add(x, y), r)` and `Add(Neg(v), r)` *is* exhaustive, and the shortcut would refuse it —
a false error on exactly the compiler-shaped code this check exists to serve. Doing it properly
means a usefulness algorithm over the pattern matrix, with the field types `TOAST-0052` now
provides. That is a slice of its own.

## Acceptance

- [x] A `match` over a closed union whose arms do not cover every variant is an error
- [x] The diagnostic names the *uncovered variants*, not just the fact of incompleteness
- [x] An explicit `default` arm satisfies the check, and is the documented opt-out
- [x] Guards do not count toward coverage — an arm that may not fire cannot complete a match,
      and the help says so, since it is the one refusal that reads like a mistake
- [ ] Nested patterns are checked to the depth they destructure — **not done**, and a
      conservative version would refuse exhaustive code; see the first slice
- [x] Adding a variant to a union produces one diagnostic per uncovered site, and a test
      pins that
- [x] Matching over a non-union value is unaffected — 32 repo files and 24 of the author's
      own bind clean, and the suite needed no changes
- [x] `§Match Expressions` states the rule and the opt-out, with both listings run first

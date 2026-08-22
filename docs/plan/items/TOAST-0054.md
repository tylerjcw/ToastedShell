---
id: TOAST-0054
title: "A `match` over a closed union is not checked for exhaustiveness"
status: proposed
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

## Acceptance

- [ ] A `match` over a closed union whose arms do not cover every variant is an error
- [ ] The diagnostic names the *uncovered variants*, not just the fact of incompleteness
- [ ] An explicit `else`/`default` arm satisfies the check, and is the documented opt-out
- [ ] Guards do not count toward coverage — an arm that may not fire cannot complete a match
- [ ] Nested patterns are checked to the depth they destructure
- [ ] Adding a variant to a union produces one diagnostic per uncovered site, and a test
      pins that
- [ ] Matching over a non-union value is unaffected — no new diagnostics on shell code
- [ ] `§Match Expressions` states the rule and the opt-out

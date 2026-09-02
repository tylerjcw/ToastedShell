---
id: TOAST-0082
title: "There is no compile-time value form, and overloading `const` would break its useful runtime meaning"
status: proposed
area: toast
priority: 3
opened: 2026-08-28
---

## Problem

`const` is intentionally a runtime immutable binding. Its initializer may read the clock, call a
function or perform any other ordinary operation once:

```tosh
const StartedAt = (date)
```

The specification explicitly reserves compile-time constants for a future keyword. Without one,
generated tables, target-dependent layouts, validated static data and specialization either run
at startup or move into C#/the build system. Reusing `const` would make existing source lie.

## Candidate surface and staged scope

```tosh
comptime PageSize = 4 * 1024
comptime Magic = [0x54, 0x4f, 0x53, 0x48]

func mask(bits: int) => (1 << $bits) - 1
comptime ByteMask = mask(8)          # allowed once the call is proven pure
```

The first implementation should accept literals, operators over compile-time values, aggregate
construction and references to earlier `comptime` names. Calls can follow after `TOAST-0087`
provides a transitive proof that the callee has no effects. This stages Nim/Zig-like computation
without making arbitrary host execution part of the compiler's trusted input on day one.

## Reproducibility boundary

Compile-time code cannot read ambient time, random state, environment variables, the network or
undeclared files. A future build-input mechanism may expose an explicitly hashed input; ambient
access is a diagnostic. CLR reflection and native calls are target capabilities, not constant
evaluation shortcuts.

## Acceptance

- [ ] `comptime` (or the finally chosen distinct keyword) has a grammar and bound-node form
- [ ] The constant-expression subset includes literals, pure operators, aggregates and references
      to earlier compile-time values
- [ ] A disallowed expression reports the operation and effect that made it non-constant
- [ ] Evaluation has deterministic overflow, resource-limit and target-width rules
- [ ] Ambient time, randomness, environment, undeclared filesystem, network, CLR and native access
      are rejected
- [ ] Compile-time results are serialized into compiled artifacts and are not recomputed at startup
- [ ] Interpreted scripts observe the same value and diagnostic rules as compiled scripts
- [ ] Public compile-time calls require `TOAST-0087`'s proven empty effect set
- [ ] `const` remains a runtime immutable binding, and migration/help text distinguishes the forms
- [ ] Evaluation is bounded against runaway recursion, allocation and expansion

## Existing decision

The legacy `TS-P3-02` item and the older decision ledger describe a period when `let` was intended
to take runtime immutability and `const` compile-time evaluation. The current specification has
since given the runtime role to `const`. This item follows that shipped contract and adds a new
name rather than reviving the collision.

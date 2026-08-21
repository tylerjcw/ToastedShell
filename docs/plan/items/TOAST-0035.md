---
id: TOAST-0035
title: "Source replay and implicit dynamic are how the compiler handles what it cannot emit"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Phase B's second bullet: "remove compiler-subset source replay and implicit dynamic
fallbacks". Both mechanisms are live.

**Source replay** — when the emitter cannot produce IL for a construct, the construct is
left to be re-executed by the tree-walking evaluator at runtime. `TOAST-0030` showed what
that costs when the fallback itself does not work: `class E extends Error { }` was handed
to replay and failed at runtime with "Command 'class' was not found", so a declaration that
runs interpreted did not run at all compiled. It is referenced in **61 places** across
`src/Tosh.Compiler`, `src/Tosh.Compiler.Runtime` and `src/Tosh.Language`, under the name
"Tier 3".

**Implicit dynamic** — `--compile-allow-dynamic` exists so a program with un-inferrable
locals can compile anyway, by falling back to dynamic dispatch.

## Measured 2026-08-21

- Eleven distinct "unsupported *X*" emitter diagnostics: assignment operator, block body
  stage, destructuring pattern, expression, first pipeline stage, interpolated part,
  literal type, member assignment target, numeric op, pipeline stage, statement.
- Constructs explicitly documented as staying Tier 3 include native `require` blocks and
  bind blocks.
- The readiness probe (`TOAST-0038`) hits `implicit_dynamic` four times.

## Why the order matters

Removing the fallbacks *first* would simply make working programs stop compiling.
`TOAST-0034` is the prerequisite for the dynamic half — with inference propagating,
most `implicit_dynamic` sites disappear rather than needing annotation — and the emitter
gaps behind the eleven "unsupported" messages are the prerequisite for the replay half.

The honest sequence is: make the fallback unnecessary, then remove it, then keep it removed
with a strict-mode gate.

## Acceptance

- [ ] Every "unsupported" emitter diagnostic is enumerated with a program that triggers it
- [ ] Each is either implemented, or recorded as a deliberate and documented exclusion
- [ ] `--compile-allow-dynamic` is not needed by any program in `examples/` or `bench/`
- [ ] A strict profile fails the build rather than replaying source
- [ ] A negative control

## Notes

`TOAST-0030` closed the one replay path that was actively wrong. This item is about the
mechanism rather than a single use of it.

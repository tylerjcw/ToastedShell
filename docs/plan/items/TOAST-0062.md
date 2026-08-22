---
id: TOAST-0062
title: "A hot loop cannot state that it does not allocate, and value types are copied where a reference would do"
status: proposed
area: toast
priority: 3
opened: 2026-08-22
---

## Problem

Tōast runs on a garbage collector, which is the right choice for the language
(`docs/SELF_HOSTING_RFC.md` treats a collector as part of the runtime kernel for every
target). What the language does not offer is any way to write code that stays out of its way.

Two specific gaps:

**Nothing can assert the absence of allocation.** A frame loop, an audio callback, a
lock-free producer or an interrupt-shaped handler all have the same requirement — this
function must not allocate — and there is no way to say it, so there is no way to find out
it stopped being true except by measuring drift after the fact.

**Value types are copied where a reference would do.** There is no `readonly struct`, no `in`
parameter, and no `ref` local. A large `struct` passed to a function is copied, and passed
again is copied again. `§Struct Definitions` has read-only-by-default fields and a `fluid`
opt-out, which is the mutability half; the passing half has no spelling.

## Why `#[no_alloc]` is the item worth having

Almost no garbage-collected language offers a compile-time no-allocation guarantee, and it is
the single feature that would make Tōast credible inside a frame budget. It is a build
**failure**, not a warning and not a runtime counter: the function allocates, the build stops,
naming the expression that allocated.

It is also more broadly useful than games. The same annotation is what makes an audio path,
a signal handler, or a tight parsing loop reviewable.

The analysis it needs is real but bounded — boxing, closure capture, array and string
creation, and calls to anything not itself marked. The last of those is what makes it
tractable: the property is transitive and declared, so it does not need whole-program
analysis.

## Dependencies

`TOAST-0055`'s `where T: struct` is the prerequisite for the generic half — without it a
generic container over a value type boxes, and no annotation can be satisfied. `TOAST-0057`'s
spans are what most no-allocation code is written against. `ref` locals and returns are filed
with `TOAST-0059`, since they arrive with the pointer work.

## Acceptance

- [ ] `readonly struct`, and defensive copies are elided for one
- [ ] `in` parameters, passing a value type by reference without copying
- [ ] `#[no_alloc]` on a function is a build failure when the body can allocate
- [ ] The diagnostic names the allocating expression and the reason — boxing, capture,
      literal, or an unmarked callee
- [ ] The property is transitive: a marked function may only call marked functions
- [ ] Boxing at the static/dynamic tier boundary is one of the reported causes, which makes
      the boundary visible where it costs
- [ ] A fixture loop runs a fixed number of iterations at zero bytes allocated, measured
      rather than asserted by inspection
- [ ] Interpreted and compiled agree on what is rejected

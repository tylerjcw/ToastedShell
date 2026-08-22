---
id: TOAST-0057
title: "`span<T>` is not a language type, so slicing a string or a buffer always allocates"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

There is no `span<T>` or `readonlyspan<T>` in the type model, no slicing syntax that yields
a view rather than a copy, and no `stackalloc`. Every operation that takes part of something
larger produces a new allocation.

`TOAST-0048`'s audit lists what the type model resolves; a span shape is absent from both the
"works" and "orphans" columns — there is no representation to reach.

`TOAST-0012` covers the adjacent but different gap: `Span<T>` and `Memory<T>` are not accepted
as *native parameter shapes*, so a `bind native` signature must take a pointer and a length.
That item is about crossing the FFI boundary. This one is about the language having the type
at all, on the managed side, before any binding is involved.

## Who is blocked

A lexer is the clearest case, and it is the one the self-hosting work runs into first: tokenising
a source file means taking thousands of substrings, and every one of them is a fresh `string`.
The RFC's compiler inventory (`TokenKind` and `Token`, symbols, parent-linked scopes) is built
on exactly this operation.

The same shape appears in the two other directions the language is being pointed:

- **Systems** — viewing an `alloc` buffer or a `raw struct`'s inline array without copying it
  out, which is currently the only way to read one.
- **Games** — writing into a mapped vertex or index buffer, where the copy is the frame budget.

## Design notes

The CLR provides all of this; the work is surfacing, not building. Two decisions are not free:

1. **Lifetime.** `Span<T>` is a `ref struct` on the CLR — it cannot be boxed, stored in a
   field, captured by a closure, or used across an `await`. Tōast has no concept for that
   restriction today. The honest options are to enforce it (a new kind of type, with
   diagnostics) or to expose only `readonlyspan<T>` over managed memory where the restriction
   is easier to state.
2. **The dynamic tier.** A span must not leak into a pipeline, where it would be boxed or
   outlive its backing store. The boundary between the static and dynamic tiers is the real
   subject here, and this item is where that boundary first has to be written down.

## Acceptance

- [ ] `span<T>` and `readonlyspan<T>` resolve in the type model and are writable in annotations
- [ ] Slicing an array, a `string`, and an `alloc` buffer yields a view, and a test pins that
      no allocation occurs
- [ ] `stackalloc` for a scoped temporary, with its scope stated
- [ ] The lifetime restriction is enforced with diagnostics rather than documented and unchecked —
      a span cannot be stored, captured, or returned past its backing store
- [ ] A span reaching the dynamic tier is a diagnostic, not a silent box
- [ ] Interpreted and compiled agree, in the differential corpus
- [ ] `§Type System` carries the type and its restrictions; the relationship to `TOAST-0012`'s
      native parameter shapes is stated in one place

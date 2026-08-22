---
id: TOAST-0059
title: "Native memory is reached through untyped `ptr`, and nothing marks where safety ends"
status: proposed
area: toast
priority: 3
opened: 2026-08-22
---

## Problem

Two gaps in the same area, filed together because the second is what makes the first
tolerable.

**`ptr` is untyped.** It aliases `System.IntPtr`. There is no `ptr<T>`, no dereference, no
pointer arithmetic, and no pinning. Code that walks a native linked structure computes byte
offsets by hand and reads through `read-buffer`, which is both error-prone and unreadable —
`TOSH-0007` (the marshalled `statvfs` that was 24 bytes short) is what that class of
arithmetic costs when it goes wrong.

**Nothing marks the unsafe surface.** `bind native`, `alloc`, `raw struct`, and raw pointer
work are available anywhere, in a language that is also an interactive shell. There is no
boundary a reader, a reviewer, or a tool can use to tell code that can corrupt the process
from code that cannot.

## Why the boundary earns its place here

An `unsafe` block is usually argued for on safety grounds. In this language it has two more
concrete uses:

- **`--sandbox` has nothing to enforce.** A capability model needs a syntactic surface to
  restrict, and `docs/SELF_HOSTING_RFC.md` already defines a capability model for the target
  profiles. `unsafe` is the natural unit.
- **The `no_clr` and `native` profiles need to know.** Which constructs are available differs
  per target, and a marked region is where that check belongs.

Typed pointers make the boundary worth drawing: they are what people would write *inside* it.

## Acceptance

- [ ] `ptr<T>` resolves, is writable in annotations, and interoperates with `bind native`
- [ ] Dereference, `p + n`, and `p[i]`, with element-size arithmetic rather than byte
      arithmetic
- [ ] `pin` for taking the address of a managed object, with its scope stated
- [ ] `ref` locals and `ref` returns, so a caller can be handed an interior reference
- [ ] An `unsafe` block and `unsafe func`, and the set of constructs that require one is
      enumerated in the specification
- [ ] Safe code calling into unsafe code is diagnosed where the specification says it should be
- [ ] The capability model in the RFC references the boundary rather than describing a second one
- [ ] `TOSH-0007`'s `statvfs` case is rewritten against typed pointers as a fixture

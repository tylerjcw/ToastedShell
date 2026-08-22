---
id: TOAST-0060
title: "Writing a compiler in Tōast means writing arenas, derivation and interning by hand"
status: proposed
area: toast
priority: 3
opened: 2026-08-22
---

## Problem

`TOAST-0052`, `TOAST-0053` and `TOAST-0054` are what make a self-hosted compiler *possible*.
This item is what makes one *pleasant*, and it is filed separately because none of it blocks
the gate — each is a multiplier on the same work.

Three things a compiler needs constantly and Tōast has no answer for:

**Region allocation.** A compilation unit's syntax tree, symbol table and type graph share
one lifetime and are discarded together. A GC handles that acceptably; a region where
allocation is a pointer bump and release is one operation handles it better, and composes
with the `defer` the language already has:

```tosh
arena unit {
    var ast = (parse $source)
    ...
}   # whole region released here
```

**Structural derivation.** Equality, hashing, display and traversal are written once per node
type, mechanically, for dozens of node types. Deriving them from the declaration removes the
largest source of copy-paste bugs in any compiler codebase:

```tosh
derive Eq, Hash, Show, Visit
union Expr { Lit(value: double)  Add(left: Expr, right: Expr) }
```

`Visit` is the one specific to this domain: a generated traversal over a closed union is
what a compiler's passes are written against, and hand-writing it per pass is where node
types get silently skipped.

**Interning.** A compiler compares identifiers constantly. That wants an interned `Symbol`
type, and dictionaries keyed by user-supplied hash and equality — which needs `TOAST-0055`'s
trait bounds — and `readonlyspan<char>` lookup that does not allocate, which needs
`TOAST-0057`.

## Ordering

Depends on `TOAST-0052` (there must be typed unions to derive over), `TOAST-0055` (bounds for
the dictionary), and `TOAST-0057` (spans for the lookup). Filed at priority 3 because the
self-hosted compiler can be written without any of it — just larger, slower, and with the
traversal bugs that hand-written visitors produce.

## Acceptance

- [ ] `arena` scopes with bump allocation and single-operation release, composing with `defer`
- [ ] A value cannot outlive its arena, and that is diagnosed rather than documented
- [ ] `derive` for `Eq`, `Hash`, `Show` on records, structs and unions
- [ ] `derive Visit` generates a traversal over a closed union, and adding a variant changes
      the generated traversal
- [ ] An interned `Symbol` type with constant-time equality
- [ ] Dictionaries accept user-supplied hash and equality through trait bounds
- [ ] `readonlyspan<char>` keys look up without allocating, and a test pins that
- [ ] `bench/probes/compiler_shape.tosh` uses all three, and the line count before and after
      is recorded here

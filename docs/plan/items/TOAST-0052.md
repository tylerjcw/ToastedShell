---
id: TOAST-0052
title: "A union variant has no field types, and a union cannot be generic"
status: proposed
area: toast
priority: 1
opened: 2026-08-22
---

## Problem

`§Union Definitions` gives variants untyped fields:

```tosh
union Result {
    Ok(value)
    Error(message)
    None
}
```

`value` and `message` have no declared type, and there is no way to give them one. A
generic union does not parse at all:

```tosh
union Result<T, E> { Ok(T) Error(E) }
# parse error: insert '|' before the next command
```

So the two shapes a compiler is built from cannot be written:

```tosh
union Result<T, E> { Ok(T)  Error(E) }
union Expr        { Lit(value: double)  Add(left: Expr, right: Expr) }
```

## Why this blocks Gate A rather than merely limiting it

`docs/SELF_HOSTING_RFC.md` says a `union` "defines a closed tagged sum suitable for
exhaustive matching", and that "closed compiler trees prefer unions and records because
matches can be exhaustive and native layouts are predictable". Neither property is
reachable from a variant whose fields are untyped: there is nothing to check exhaustively
against, and no layout to predict.

The RFC's own inventory of what the self-hosted compiler needs — `TokenKind` and `Token`,
closed syntax-node unions, bound-node unions, lowered IR instructions — is a list of
generic and field-typed unions. Written against today's form, every one of them is a bag
of `dynamic` reached through a string tag.

This item is the first of three. `TOAST-0053` is the matching side and `TOAST-0054` is the
checking side; neither is meaningful without this one.

## The decision this needs

**How is a union represented?** The choice is not free and it lands on the self-hosted
compiler's hot path:

1. **Class hierarchy per variant** — F#'s choice. Uniform, handles recursion naturally,
   costs one allocation and a virtual dispatch per value. An AST walk allocates per node.
2. **Struct with a tag and overlapped payload** — no allocation, cache-friendly, awkward
   for recursive variants (`Add(left: Expr, right: Expr)` cannot contain itself by value).
3. **Both, chosen by declaration** — `struct union` for flat closed sets like `TokenKind`,
   class-backed for recursive trees.

Option 3 mirrors the `struct` / `raw struct` split the language already draws, and that
split is the strongest precedent in the language for "two memory models, two declaration
kinds, no ambiguity". `TOAST-0048` establishes that generic *instantiation* already works
(`Box<int>`, `Box<Box<int>>`), so the generic half is a grammar and binding gap rather than
a type-model gap.

## Acceptance

- [ ] A variant field may carry a type annotation, positionally (`Ok(T)`) and by name
      (`Lit(value: double)`)
- [ ] A union may declare type parameters, and they are usable in variant field types
- [ ] A union may be recursive — `Add(left: Expr, right: Expr)` declares and constructs
- [ ] Constructing a variant checks its field types, and the diagnostic names the field
- [ ] The representation decision is recorded here with its reasoning, and `§Union
      Definitions` states it
- [ ] Generic unions instantiate the way generic classes already do, including nesting
- [ ] Interpreted and compiled agree, in the differential corpus
- [ ] `bench/probes/compiler_shape.tosh` declares its syntax-node union in the real form

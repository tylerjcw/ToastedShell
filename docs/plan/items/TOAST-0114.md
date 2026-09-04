---
id: TOAST-0114
title: "A struct is invisible to the editor: no outline entry, no hover, no completion"
status: complete
area: toast
priority: 3
opened: 2026-09-04
---

## Problem

`DeclarationIndex` had no case for `StructDefinitionStatementSyntax`. Not an incomplete one — no
case at all, so a struct was simply absent from every surface built on the index.

Measured against a file declaring two structs and constructing both:

| asked | answered |
|---|---|
| completion inside `new Vec2 {\| … \|}` | 372 generic entries; no `X`, no `Y` |
| hover on `Vec2` | `null` |
| document outline | `[v]` — the variable, and neither struct |
| go-to-definition on a struct name | nothing to go to |

Found while closing `TOAST-0091`, which taught the index about declared types for typed-literal
completion: classes and records answered, structs did not, and the reason turned out to be that
they had never been indexed for anything.

## What made it more than a missing case

A struct carries **both** member shapes, and is the only declaration that does:

```tosh
struct Pair(A: int, B: int)     # record-shaped fields
struct Vec2 {                   # class-shaped members
    prop X = 0
    prop Y = 0
}
```

So the initializable-member lookup, which had been "a class means properties, otherwise fields",
becomes a set of kinds rather than one — a struct is the case that cannot be answered by picking
a single member kind.

## Acceptance

- [x] A braced struct offers its properties in a typed literal
- [x] A parenthesised struct offers its fields
- [x] A struct appears in the document outline
- [x] Hover on a struct names it as a struct
- [x] A struct is offered where type names are
- [x] An imported struct is registered, like an imported class or record
- [x] Classes and records are unaffected

## Notes

Two things cost time and are worth recording.

**`struct Vec2 { X: int }` is not valid syntax**, and the first probe was written that way. A
braced struct body takes `prop` and `func` like a class; only the parenthesised form declares bare
fields. The parser says so clearly — *"write 'prop', 'func', or a constructor here"* — but the
probe was reading the index rather than running the file, so it saw a malformed declaration and
reported a puzzling one-item completion instead of an error.

**The duplicate-arm trap from the union work repeated exactly.** Patching a `switch` by replacing
a 16-space-indented arm also matches inside a 20-space-indented one, producing two arms and
`CS8510: the pattern is unreachable`. It was the same edit shape, in the same file, a few days
apart. Anchoring on indentation is the fix; noticing that the compiler catches it is the
consolation.

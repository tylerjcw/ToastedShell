---
id: TOAST-0048
title: "The type model has three shapes nothing can produce, and four the grammar cannot spell"
status: proposed
area: toast
priority: 3
opened: 2026-08-21
---

## What the audit found

`BoundType` was resolved against a program declaring one of each user type, and every
spelling below was measured rather than assumed.

### Works — a fuller model than expected

| Group | Resolves |
|---|---|
| Primitives | `int`, `long`, `double`, `decimal`, `bool`, `string`, `char`, `object` |
| Sentinels | `dynamic`, `void` |
| Nullable | `int?`, `string?`, `K?` |
| Arrays | `int[]`, `int[][]` |
| Collections | `list<T>`, `array<T>`, `set<T>`, `dict<K,V>`, and nested |
| Tuples | `(int, string)`, `(int, string, bool)`, `()` |
| User declarations | class, record, struct, enum, union, interface, trait — all seven |
| Refinements | `type PosInt = int where _ > 0` |
| Generic instantiation | `Box<int>`, `Box<K>`, `Box<Box<int>>` |

### Orphans — representable, unreachable

**`FunctionType`** exists in `BoundType.cs`, with a `DisplayName` of
`(int, string) -> bool`, and **nothing constructs one**: no `new FunctionType` anywhere, and
`TypeNameResolver` never mentions it. The type-name grammar has `Named`, `Generic`, `Array`,
`Nullable` and `Tuple` nodes and no function node, so `func(int) -> int` cannot parse.

This is a **correction to `TOAST-0036`**, which says there is no concrete function type.
The representation is already there; what is missing is a way to write one and something to
build it. That makes the item smaller than filed.

**`StreamType`** is the same story: `stream<int>` resolves to `dynamic`.

**`TupleType` is reachable from the resolver and not from the parser.** `(int, string)`
resolves to a proper `TupleType` — but `func f() -> (int, string)` and
`var t: (int, string)` are both parse errors (`tosh.parser.expected_type_name`). So the type
exists, resolves, and cannot be written in either position where it would be used. Multiple
returns are a compiler staple: a value and its diagnostics, a token and its position.

**`func` resolves to `System.Func\`1`.** Not an alias — the platform-index fallback added in
`TOAST-0034` finds the CLR type by simple name. So `var f: func` is *concrete and wrong*,
which is worse than dynamic. Almost certainly not intended.

### Gaps — no representation at all

- **Bottom type** — `TOAST-0047`.
- **Anonymous unions and intersections.** `int|string` and `int & string` resolve to
  `dynamic`. Tōast has *declared* unions (`union U { P, Q }`) but no set operations on types.
- **Literal types.** `1`, `"a"`, `true` resolve to `dynamic`. Relevant to refinements, which
  already narrow a base type by predicate.

## What is worth having, from a compiler-building perspective

The probe (`bench/probes/compiler_shape.tosh`) is the evidence for what compiler-shaped code
actually needs, and it needed exactly two things the model lacks:

1. **A function type**, for passing a visitor or a continuation. Highest value, and the
   representation exists — `TOAST-0036`.
2. **A bottom type**, so `default => throw …` does not poison an inferred result —
   `TOAST-0047`.

3. **Tuple annotations**, for the two-value returns a compiler makes constantly. The type
   exists; only the parser is missing.
4. **Record update** — `node with { Left = $newLeft }` is how a tree transform is written,
   and it is a parse error today. A compiler rebuilds trees more than it mutates them.

Unions, intersections and literal types are further off. A compiler's AST is a *closed* set
of node kinds, which Tōast's declared `union` already expresses; anonymous unions would be
convenience rather than capability.

**Bigger than any of these is `TOAST-0049`** — recursion is capped at 128 frames, so the
probe's own parser fails on forty nested parentheses. A type system gap makes code awkward;
that one makes a class of input impossible.

## Acceptance

- [ ] `stream<T>` either parses or is removed from the model
- [ ] `func` stops resolving to `System.Func\`1`
- [ ] `FunctionType` is reachable from source, or removed — `TOAST-0036` decides which
- [ ] Anonymous unions, intersections and literal types are each recorded as intended or
      deliberately excluded, rather than left silently `dynamic`
- [ ] `docs/spec/` carries the type model, which it currently does not state in one place
- [ ] A negative control

## Notes

Raised by the user asking whether the type system is complete. The honest answer is that it
is fuller than expected and has three shapes nothing can produce — which is a different kind
of incompleteness from a missing feature, and easier to fix.

---
id: TOAST-0061
title: "The value types graphics and physics code is written in have no Tōast spelling"
status: partial
area: toast
priority: 3
opened: 2026-08-22
---

## Problem

`§Built-in Type Aliases` blesses over forty CLR types, including a full set of physical
quantities and three numeric shell types — `Vector`, `Matrix`, `Complex`. None of them is
the shape graphics or physics code is written in.

`Vector` is "a shell-native dense vector of `double` values" implementing
`IEnumerable<double>`. For numeric and data work that is the right design. For a transform
hierarchy it is close to worst case: heap-allocated, boxed, double-precision and
variable-length, where the domain wants twelve bytes on the stack. A scene graph built on it
allocates per node per frame.

The types that *are* the right shape — `System.Numerics.Vector3`, `Quaternion`,
`Matrix4x4` — are reachable, but only by their CLR names, through a `using`, with no alias,
no rendering, and no place in the type table.

## What this item is not

It is not the blocker. `TOAST-0051` is: until operator dispatch consults CLR `op_*` methods,
`$a + $b` fails on all of these types and an alias would be sugar over a mechanism that does
not work. `TOAST-0056` completes the surface with unary and indexer operators.

This item is what those two make worth doing.

## Scope

**Aliases**, mapping to `System.Numerics` so they are SIMD-accelerated and interoperate with
existing .NET graphics stacks without a shim: `Vector2`, `Vector3`, `Vector4`, `Quaternion`,
`Matrix3x2`, `Matrix4x4`, and shell-side `Rect`, `AABB`, `Ray`, `Plane`, `Color`.

**The existing names do not move.** `Vector`, `Matrix` and `Complex` keep their meaning.
These are different types for different jobs, and merging them would produce something bad
at both — the same reasoning that keeps `struct` and `raw struct` separate.

**Smaller alias gaps found alongside**: `Half`, `Int128` and `UInt128` have no alias, and
`nint`/`nuint` exist only as native-signature spellings rather than as general types. `var h:
Half = 1.5` currently fails with "the value does not match 'Half'".

**Swizzling** — `$v.xy`, `$v.xzy`, `$c.rgb` — as a compiler rewrite over the blessed types.
Pure ergonomics, and the single most-used syntax in shader-adjacent code. Wants the
expression-layer work in `TS-P2-11` to have landed.

**SIMD** — `Vector128<T>`, `Vector256<T>` and the intrinsic surface, for the culling,
skinning and particle work that is written against them.

## Acceptance

- [x] The `System.Numerics` value types have aliases and appear in the type table
- [~] `Half`, `Int128`, `UInt128`, `nint`, `nuint` are general aliases, and `var h: Half = 1.5`
      — *aliases done; the assignment works but warns, which is [`TOAST-0138`](TOAST-0138.md)*
      binds
- [x] `Vector`, `Matrix` and `Complex` are unchanged, and the specification states why the two
      families are separate
- [x] Arithmetic on the blessed types works through `TOAST-0051`, not a special case
- [x] They render as values rather than as enumerables — `Vector3(1, 2, 3)`, not a table of
      three cells
- [ ] Swizzle accessors on the vector and colour types, resolved at compile time
- [ ] `Vector128<T>`/`Vector256<T>` are reachable and annotatable
- [ ] A rotating-transform fixture allocates zero bytes per frame, measured

## Done — 2026-09-20

**Aliases.** `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Matrix3x2`, `Matrix4x4` and
`Plane`, plus `Half`, `Int128`, `UInt128`, `nint` and `nuint`, are entries in
`DotNetTypeResolver.Aliases` and rows in `§Built-in Type Aliases`.

They resolved before this — but only through the platform-index fallback that finds a CLR type
by simple name, which is the same mechanism that made `func` resolve to `System.Func`1`,
concrete and wrong ([`TOAST-0048`](TOAST-0048.md)). A name worth relying on is worth aliasing
explicitly rather than leaving to a lookup that can find something else.

`nint` and `ptr` name the same CLR type and are kept apart deliberately: one says
"pointer-sized integer", the other says "pointer", and an annotation should be able to say
which it meant.

**Rendering.** A vector or quaternion prints as a value named by its type — `Vector3(1, 2, 3)`
— where it used to print a transposed four-row table to say three numbers. The type leads the
form because `(1, 2, 3)` alone does not say whether a fourth component was dropped or was never
there. A matrix keeps its table; sixteen numbers on one line is not a reading of anything.

**Arithmetic** already went through `TOAST-0051`'s CLR `op_*` fallback rather than a special
case, and now has a test saying so.

**The specification** states why the two families coexist, beside the `Vector` prose: one is
heap-allocated, variable-length and double-precision for numeric and data work; the other is
fixed, single-precision and by value for a transform hierarchy, which allocates per node per
frame if built on the first.

### A test that measured the wrong layer

The rendering tests first asserted against `results[^1].ToString()`, which is the CLR
`ToString()` — `<1, 2, 3>` for a `Vector3`, `{X:0 Y:0 Z:0 W:1}` for a `Quaternion`. A display
profile applies in the display pipeline, so those assertions would have passed whatever the
profile did. They render through `DisplayEngine` now, and the reason is written beside the
helper.

## Still open

Swizzle accessors, `Vector128<T>`/`Vector256<T>`, and the zero-allocation rotating-transform
fixture — each its own piece of work. `var h: Half = 1.5` warning is
[`TOAST-0138`](TOAST-0138.md), which is not specific to these types.
